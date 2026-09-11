using System.Text;
using System.Text.RegularExpressions;

// 独立命令行工具：直接解析 Data/Configs/*.tres，按与游戏内 CombatRating 相同的公式输出单位战斗力评分。
// 用法：dotnet run --project Tools/CombatRatingTool -- [配置目录]
// 默认目录：D:\DEV\RTSarcade\Data\Configs

var dir = args.Length > 0 ? args[0] : @"D:\DEV\RTSarcade\Data\Configs";

var units = new Dictionary<string, UnitCfg>();
var weapons = new Dictionary<string, WeaponCfg>();

foreach (var file in Directory.GetFiles(dir, "*.tres"))
{
    string text = File.ReadAllText(file);
    if (text.Contains("script_class=\"UnitConfig\""))
    {
        var u = ParseUnit(Path.GetFileNameWithoutExtension(file), text);
        if (u != null) units[u.Id] = u;
    }
    else if (text.Contains("script_class=\"WeaponConfig\""))
    {
        var w = ParseWeapon(Path.GetFileNameWithoutExtension(file), text);
        if (w != null) weapons[w.Id] = w;
    }
}

var final = units.Values
    .Select(u => (u.Id, Dps: ComputeRawDps(u, weapons), Score: Compute(u, weapons), Cost: ComputeCost(u)))
    .Select(t => (t.Id, t.Dps, t.Score, t.Cost, Value: t.Cost > 0f ? t.Score / t.Cost : 0f))
    .OrderByDescending(x => x.Value)
    .ToList();

Console.WriteLine($"== 单位性价比（战力/成本，成本=资源折算，气×1.5，参考：步枪兵 ≈ 100）==");
Console.WriteLine($"{"单位",-22} {"DPS",8} {"战力",7} {"成本",7} {"性价比",7}");
foreach (var (id, dps, score, cost, value) in final)
    Console.WriteLine(cost > 0f
        ? $"{id,-22} {dps,8:F1} {score,7} {cost,7:F0} {value,7:F2}"
        : $"{id,-22} {dps,8:F1} {score,7} {"免费",7} {"-",7}");

static float ComputeCost(UnitCfg cfg)
{
    // 资源权重：Gas(8) ×1.5，其余按 1.0（金属 0/生物质 5/纳米虫 6/数据 9 等）
    float cost = 0f;
    foreach (var (type, amount) in cfg.Costs)
    {
        float w = type == 8 ? 1.5f : 1f;
        cost += amount * w;
    }
    return cost;
}

static float ComputeRawDps(UnitCfg cfg, Dictionary<string, WeaponCfg> weapons)
{
    float plasma = PlasmaSkillDps(cfg, weapons);
    if (cfg.MultiShotTargets > 1)
    {
        if (cfg.WeaponIds.Count == 0 || !weapons.TryGetValue(cfg.WeaponIds[0], out var main))
            return 0f;
        return (main.Damage + main.BonusDamage * 0.5f)
            * cfg.MultiShotTargets
            * (20f / Math.Max(1, main.CooldownTicks))
            + plasma;
    }
    float dps = 0f;
    foreach (var wid in cfg.WeaponIds)
    {
        if (!weapons.TryGetValue(wid, out var w)) continue;
        float rate = 20f / Math.Max(1, w.CooldownTicks);
        dps += w.Damage * rate + w.BonusDamage * 0.5f * rate;
    }
    return dps + plasma;
}

// 电浆炮技能（PlasmaArtillery）：基础伤害 + 长宽²伤害（按平均 2×2 目标折算）÷ 完整循环
static float PlasmaSkillDps(UnitCfg cfg, Dictionary<string, WeaponCfg> weapons)
{
    if (cfg.PlasmaCooldownSeconds <= 0f || !weapons.TryGetValue("PlasmaArtillery", out var pa))
        return 0f;
    float cycle = Math.Max(1f, cfg.PlasmaWindupSeconds + cfg.PlasmaRecoverySeconds + cfg.PlasmaCooldownSeconds);
    return (pa.Damage + pa.ImpactDamagePerFootprintSq * 4f) / cycle;
}

static UnitCfg? ParseUnit(string id, string text)
{
    var u = new UnitCfg { Id = id };
    u.MaxHp = GetF(text, "MaxHp", 100f);
    u.MaxShield = GetF(text, "MaxShield", 0f);
    u.MoveSpeed = (int)GetF(text, "MoveSpeed", 60f);
    u.DefKinetic = GetF(text, "DefKinetic", 0f);
    u.DefThermal = GetF(text, "DefThermal", 0f);
    u.DefExplosive = GetF(text, "DefExplosive", 0f);
    u.DefEM = GetF(text, "DefEM", 0f);
    u.DefBeam = GetF(text, "DefBeam", 0f);
    u.MultiShotTargets = (int)GetF(text, "MultiShotTargets", 1f);
    u.DeployedMaxHpMultiplier = GetF(text, "DeployedMaxHpMultiplier", 1f);
    u.DeployedAttackRangeBonusTiles = GetF(text, "DeployedAttackRangeBonusTiles", 0f);
    u.HeroEnergyMax = GetF(text, "HeroEnergyMax", 0f);
    u.Skill1Cost = GetF(text, "Skill1Cost", 0f);
    u.Skill2Cost = GetF(text, "Skill2Cost", 0f);
    u.Skill2Damage = GetF(text, "Skill2Damage", 0f);
    u.PlasmaWindupSeconds = GetF(text, "PlasmaWindupSeconds", 0f);
    u.PlasmaRecoverySeconds = GetF(text, "PlasmaRecoverySeconds", 0f);
    u.PlasmaCooldownSeconds = GetF(text, "PlasmaCooldownSeconds", 0f);
    u.MaxAmmo = (int)GetF(text, "MaxAmmo", 0f);
    u.HealPerSecond = GetF(text, "HealPerSecond", 0f);
    u.RepairPerSecond = GetF(text, "RepairPerSecond", 0f);
    u.Costs = GetCosts(text);
    u.CanHeal = GetB(text, "CanHeal");
    u.CanRepair = GetB(text, "CanRepair");
    u.EnergyRegenInAmmoRange = GetB(text, "EnergyRegenInAmmoRange");
    u.IsWorker = GetB(text, "IsWorker");
    u.CanHarvest = GetB(text, "CanHarvest");
    u.IsHero = GetB(text, "IsHero");
    u.IsAir = GetB(text, "IsAir");
    u.CanDeploy = GetB(text, "CanDeploy");
    u.CanSwitchAmmoMode = GetB(text, "CanSwitchAmmoMode");
    u.HasLeap = GetB(text, "HasLeap");
    u.WeaponIds = GetArray(text, "WeaponIds");
    return u;
}

static WeaponCfg? ParseWeapon(string id, string text)
{
    var w = new WeaponCfg { Id = id };
    w.Damage = GetF(text, "Damage", 0f);
    w.BonusDamage = GetF(text, "BonusDamage", 0f);
    w.CooldownTicks = (int)GetF(text, "CooldownTicks", 20f);
    w.AttackRange = GetF(text, "AttackRange", 0f);
    w.ConeAngleDegrees = GetF(text, "ConeAngleDegrees", 0f);
    w.AreaRadius = GetF(text, "AreaRadius", 0f);
    w.HasAreaDamage = GetB(text, "HasAreaDamage");
    w.FootprintScaledDamage = GetB(text, "FootprintScaledDamage");
    w.ImpactDamagePerFootprintSq = GetF(text, "ImpactDamagePerFootprintSq", 0f);
    w.CanTargetAir = GetB(text, "CanTargetAir", true);
    w.CanTargetGround = GetB(text, "CanTargetGround", true);
    return w;
}

static float GetF(string text, string key, float def)
{
    var m = Regex.Match(text, $@"^\s*{key}\s*=\s*([\d.]+)\s*$", RegexOptions.Multiline);
    return m.Success && float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
}

static bool GetB(string text, string key, bool def = false)
{
    var m = Regex.Match(text, $@"^\s*{key}\s*=\s*(true|false)\s*$", RegexOptions.Multiline);
    return m.Success ? m.Groups[1].Value == "true" : def;
}

static List<string> GetArray(string text, string key)
{
    var m = Regex.Match(text, $@"^\s*{key}\s*=\s*Array\[String\]\(\s*\[(.*?)\]\s*\)", RegexOptions.Singleline | RegexOptions.Multiline);
    if (!m.Success) return new List<string>();
    return m.Groups[1].Value
        .Split(',')
        .Select(s => s.Trim().Trim('"'))
        .Where(s => s.Length > 0)
        .ToList();
}

static Dictionary<int, float> GetCosts(string text)
{
    var result = new Dictionary<int, float>();
    var m = Regex.Match(text, @"^\s*Costs\s*=\s*Dictionary\[int,\s*float\]\(\s*\{(.*?)\}\s*\)", RegexOptions.Singleline | RegexOptions.Multiline);
    if (!m.Success)
        return result;
    foreach (Match kv in Regex.Matches(m.Groups[1].Value, @"(\d+)\s*:\s*([\d.]+)"))
    {
        if (int.TryParse(kv.Groups[1].Value, out int type) &&
            float.TryParse(kv.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float amount))
            result[type] = amount;
    }
    return result;
}

static float Compute(UnitCfg cfg, Dictionary<string, WeaponCfg> weapons)
{
    bool supportOnly = cfg.WeaponIds.Count == 0 && (cfg.CanHeal || cfg.CanRepair || cfg.EnergyRegenInAmmoRange);
    if (supportOnly)
    {
        float support = (cfg.CanHeal ? cfg.HealPerSecond : 0f)
            + (cfg.CanRepair ? cfg.RepairPerSecond * 0.3f : 0f)
            + (cfg.EnergyRegenInAmmoRange ? 8f : 0f);
        return (float)Math.Round(15f + support * 1.5f);
    }

    float ehp = cfg.MaxHp + cfg.MaxShield;
    float armorMult = 1f + (cfg.DefKinetic + cfg.DefThermal + cfg.DefExplosive + cfg.DefEM + cfg.DefBeam) * 0.05f;
    ehp *= armorMult;
    if (cfg.DeployedMaxHpMultiplier > 1f)
        ehp *= 1f + (cfg.DeployedMaxHpMultiplier - 1f) * 0.6f;

    float dps = 0f;
    float maxRange = 0f;
    bool canAir = false, canGround = false;
    float plasma = PlasmaSkillDps(cfg, weapons);
    if (cfg.MultiShotTargets > 1)
    {
        if (cfg.WeaponIds.Count > 0 && weapons.TryGetValue(cfg.WeaponIds[0], out var main))
        {
            float rate = 20f / Math.Max(1, main.CooldownTicks);
            float wdps = (main.Damage + main.BonusDamage * 0.5f) * cfg.MultiShotTargets * rate;
            float rt = main.AttackRange / 64f;
            wdps *= Math.Clamp(0.55f + rt * 0.04f, 0.55f, 0.95f);
            if (main.ConeAngleDegrees > 0f) wdps *= 1f + main.ConeAngleDegrees / 180f;
            if (main.HasAreaDamage && main.AreaRadius > 0f) wdps *= 1f + Math.Min(main.AreaRadius / 192f, 2f);
            if (main.FootprintScaledDamage) wdps *= 1.5f;
            if (main.CanTargetAir) canAir = true;
            if (main.CanTargetGround) canGround = true;
            dps += wdps;
            maxRange = main.AttackRange;
        }
    }
    else
    {
        foreach (var wid in cfg.WeaponIds)
        {
            if (!weapons.TryGetValue(wid, out var w)) continue;
            float rate = 20f / Math.Max(1, w.CooldownTicks);
            float wdps = w.Damage * rate + w.BonusDamage * 0.5f * rate;
            float rt = w.AttackRange / 64f;
            wdps *= Math.Clamp(0.55f + rt * 0.04f, 0.55f, 0.95f);
            if (w.ConeAngleDegrees > 0f) wdps *= 1f + w.ConeAngleDegrees / 180f;
            if (w.HasAreaDamage && w.AreaRadius > 0f) wdps *= 1f + Math.Min(w.AreaRadius / 192f, 2f);
            if (w.FootprintScaledDamage) wdps *= 1.5f;
            if (w.CanTargetAir) canAir = true;
            if (w.CanTargetGround) canGround = true;
            dps += wdps;
            maxRange = Math.Max(maxRange, w.AttackRange);
        }
    }
    dps += plasma;
    if (canAir != canGround && (canAir || canGround)) dps *= 0.95f;

    float rangeMult = 1f + (maxRange / 64f) * 0.06f;
    if (cfg.CanDeploy && cfg.DeployedAttackRangeBonusTiles > 0f)
        rangeMult += cfg.DeployedAttackRangeBonusTiles * 0.03f;
    float speedMult = 1f + (cfg.MoveSpeed - 60f) / 400f;

    float skill = 0f;
    if (cfg.HeroEnergyMax > 0f) skill += 0.35f;
    if (cfg.Skill1Cost > 0f) skill += 0.15f;
    if (cfg.Skill2Cost > 0f || cfg.Skill2Damage > 0f) skill += 0.20f;
    if (cfg.HasLeap) skill += 0.15f;
    if (cfg.CanHeal) skill += 0.25f;
    if (cfg.CanRepair) skill += 0.15f;
    if (cfg.IsAir) skill += 0.15f;
    if (cfg.CanDeploy) skill += 0.10f;
    if (cfg.CanSwitchAmmoMode) skill += 0.10f;
    if (cfg.MaxAmmo > 0) skill -= 0.10f;

    float score = MathF.Pow(Math.Max(ehp, 1f), 0.45f)
        * MathF.Pow(Math.Max(dps, 0.01f), 0.7f)
        * rangeMult * speedMult * (1f + skill);
    score *= 3.45f;
    if (cfg.IsWorker || (cfg.CanHarvest && !cfg.IsHero))
        score *= 0.25f;
    return (float)Math.Round(score);
}

sealed class UnitCfg
{
    public string Id = "";
    public float MaxHp, MaxShield, DefKinetic, DefThermal, DefExplosive, DefEM, DefBeam;
    public float DeployedMaxHpMultiplier = 1f, DeployedAttackRangeBonusTiles;
    public float HeroEnergyMax, Skill1Cost, Skill2Cost, Skill2Damage;
    public int MoveSpeed = 60, MultiShotTargets = 1, MaxAmmo;
    public bool CanHeal, CanRepair, EnergyRegenInAmmoRange, IsWorker, CanHarvest, IsHero, IsAir, CanDeploy, CanSwitchAmmoMode, HasLeap;
    public float HealPerSecond, RepairPerSecond;
    public float PlasmaWindupSeconds, PlasmaRecoverySeconds, PlasmaCooldownSeconds;
    public List<string> WeaponIds = new();
    public Dictionary<int, float> Costs = new();
}

sealed class WeaponCfg
{
    public string Id = "";
    public float Damage, BonusDamage, AttackRange, ConeAngleDegrees, AreaRadius;
    public int CooldownTicks = 20;
    public bool HasAreaDamage, FootprintScaledDamage, CanTargetAir = true, CanTargetGround = true;
    public float ImpactDamagePerFootprintSq;
}
