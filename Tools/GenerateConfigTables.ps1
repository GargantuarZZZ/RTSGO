param(
    [string]$OutDir = "D:\DEV\RTSarcade\Data\Configs"
)

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Get-Uid {
    param([string]$ScriptRel)
    $uidPath = "D:\DEV\RTSarcade\$ScriptRel.uid"
    if (Test-Path -LiteralPath $uidPath) {
        return (Get-Content -LiteralPath $uidPath -Raw).Trim()
    }
    return ""
}

function New-Table {
    param(
        [string]$ClassName,
        [string]$ScriptRel,
        [string]$Body
    )

    $uid = Get-Uid $ScriptRel
    $ext = '[ext_resource type="Script"'
    if ($uid) { $ext += ' uid="' + $uid + '"' }
    $ext += ' path="res://' + $ScriptRel + '" id="1"]'

    return '[gd_resource type="Resource" script_class="' + $ClassName + '" load_steps=2 format=3]' + "`n`n" +
           $ext + "`n`n[resource]`nscript = ExtResource(`"1`")`n" + $Body
}

function Save-Table {
    param([string]$Name, [string]$Content)
    $path = Join-Path $OutDir ($Name + ".tres")
    [System.IO.File]::WriteAllText($path, $Content, (New-Object System.Text.UTF8Encoding($false)))
    Write-Output "GENERATED: $Name.tres"
}

# ============ Weapons ============
Save-Table "Rifle" (New-Table "WeaponConfig" "Scripts/Data/Configs/WeaponConfig.cs" @'
WeaponId = "Rifle"
DisplayName = "步枪"
Damage = 5.0
DamageType = 0
RangeType = 1
AttackRange = 400.0
CooldownTicks = 5
UsesProjectile = false
CanTargetGround = true
CanTargetStructure = true
CanTargetNeutral = true
'@)

Save-Table "BipedGan" (New-Table "WeaponConfig" "Scripts/Data/Configs/WeaponConfig.cs" @'
WeaponId = "BipedGan"
DisplayName = "双管炮"
Damage = 30.0
DamageType = 0
RangeType = 1
AttackRange = 500.0
CooldownTicks = 80
UsesProjectile = false
CanTargetGround = true
CanTargetStructure = true
CanTargetNeutral = true
'@)

Save-Table "HpGun" (New-Table "WeaponConfig" "Scripts/Data/Configs/WeaponConfig.cs" @'
WeaponId = "HpGun"
DisplayName = "速射枪"
Damage = 3.0
DamageType = 0
RangeType = 1
AttackRange = 500.0
CooldownTicks = 2
UsesProjectile = false
CanTargetGround = true
CanTargetStructure = true
CanTargetNeutral = true
'@)

Save-Table "MagicBallShooter" (New-Table "WeaponConfig" "Scripts/Data/Configs/WeaponConfig.cs" @'
WeaponId = "MagicBallShooter"
DisplayName = "魔法弹塔"
Damage = 4.0
DamageType = 1
RangeType = 1
AttackRange = 800.0
CooldownTicks = 60
UsesProjectile = true
ProjectileSpeed = 600
HitRadius = 16
CanTargetGround = true
CanTargetStructure = true
CanTargetNeutral = true
'@)

# ============ Units ============
Save-Table "SCV" (New-Table "UnitConfig" "Scripts/Data/Configs/UnitConfig.cs" @'
MaxHp = 100.0
VisionRange = 350.0
Costs = Dictionary[int, float]({
0: 50.0
})
BuildTime = 1.0
CanMove = true
SupplyCost = 1
WeaponIds = Array[String](["Rifle"])
AllowAttackMoveWithoutWeapon = true
CanHarvest = true
HarvestAmountPerCycle = 10.0
HarvestCycleTicks = 20
CarryCapacity = 50.0
HarvestableResources = Array[int]([0, 8])
CanBuild = true
BuildableStructureIds = Array[String](["CommandCenter", "BB", "VF"])
BuildPowerPercent = 100
IsWorker = true
Role = 1
'@)

Save-Table "RifleMan" (New-Table "UnitConfig" "Scripts/Data/Configs/UnitConfig.cs" @'
MaxHp = 150.0
VisionRange = 350.0
Costs = Dictionary[int, float]({
0: 100.0
})
BuildTime = 10.0
CanMove = true
SupplyCost = 1
WeaponIds = Array[String](["Rifle"])
Role = 2
'@)

Save-Table "Biped" (New-Table "UnitConfig" "Scripts/Data/Configs/UnitConfig.cs" @'
MaxHp = 400.0
VisionRange = 350.0
DefKinetic = 5.0
DefThermal = 5.0
Costs = Dictionary[int, float]({
0: 200.0
})
BuildTime = 20.0
CanMove = true
SupplyCost = 3
WeaponIds = Array[String](["BipedGan", "BipedGan"])
Role = 3
'@)

Save-Table "Hp" (New-Table "UnitConfig" "Scripts/Data/Configs/UnitConfig.cs" @'
MaxHp = 350.0
VisionRange = 350.0
DefKinetic = 5.0
DefThermal = 5.0
Costs = Dictionary[int, float]({
0: 150.0
})
BuildTime = 20.0
CanMove = true
SupplyCost = 2
WeaponIds = Array[String](["HpGun"])
Role = 4
'@)

# ============ Structures ============
Save-Table "CommandCenter" (New-Table "StructureConfig" "Scripts/Data/Configs/StructureConfig.cs" @'
MaxHp = 1000.0
VisionRange = 500.0
Costs = Dictionary[int, float]({
0: 200.0
})
BuildTime = 15.0
GridWidth = 4
GridHeight = 4
IsDropOffPoint = true
AcceptableResourceTypes = Array[int]([0, 8])
IsMainBase = true
IsProductionBuilding = true
ProductionQueueSize = 5
CanSetRallyPoint = true
TrainableUnitIds = Array[String](["SCV"])
'@)

Save-Table "BB" (New-Table "StructureConfig" "Scripts/Data/Configs/StructureConfig.cs" @'
MaxHp = 500.0
VisionRange = 400.0
Costs = Dictionary[int, float]({
0: 150.0
})
BuildTime = 10.0
GridWidth = 2
GridHeight = 2
IsProductionBuilding = true
ProductionQueueSize = 5
TrainableUnitIds = Array[String](["RifleMan"])
'@)

Save-Table "VF" (New-Table "StructureConfig" "Scripts/Data/Configs/StructureConfig.cs" @'
MaxHp = 500.0
VisionRange = 400.0
Costs = Dictionary[int, float]({
0: 150.0,
8: 100.0
})
BuildTime = 10.0
GridWidth = 3
GridHeight = 3
IsProductionBuilding = true
ProductionQueueSize = 5
TrainableUnitIds = Array[String](["Biped", "Hp"])
'@)

Save-Table "NanoCore" (New-Table "StructureConfig" "Scripts/Data/Configs/StructureConfig.cs" @'
MaxHp = 1000.0
VisionRange = 500.0
Costs = Dictionary[int, float]({
0: 300.0,
8: 200.0
})
BuildTime = 20.0
GridWidth = 4
GridHeight = 4
RequiresCreep = true
RequiredCreepType = 3
IsDropOffPoint = true
AcceptableResourceTypes = Array[int]([0, 8])
IsProductionBuilding = true
ProductionQueueSize = 5
TrainableUnitIds = Array[String](["SCV"])
'@)

Save-Table "Tower" (New-Table "StructureConfig" "Scripts/Data/Configs/StructureConfig.cs" @'
MaxHp = 1000.0
VisionRange = 800.0
Costs = Dictionary[int, float]({
0: 100.0
})
BuildTime = 10.0
GridWidth = 1
GridHeight = 1
IsDefenseBuilding = true
WeaponIds = Array[String](["MagicBallShooter"])
'@)

Save-Table "Shrine" (New-Table "StructureConfig" "Scripts/Data/Configs/StructureConfig.cs" @'
MaxHp = 1000.0
VisionRange = 250.0
GridWidth = 2
GridHeight = 2
IsInteractiveBuilding = true
'@)

Save-Table "IronOre" (New-Table "StructureConfig" "Scripts/Data/Configs/StructureConfig.cs" @'
MaxHp = 100.0
VisionRange = 0.0
GridWidth = 1
GridHeight = 1
IsResourceBuilding = true
ResourceType = 0
ResourceAmount = 4500.0
'@)

Save-Table "GasSpring" (New-Table "StructureConfig" "Scripts/Data/Configs/StructureConfig.cs" @'
MaxHp = 100.0
VisionRange = 0.0
GridWidth = 1
GridHeight = 1
IsResourceBuilding = true
ResourceType = 8
ResourceAmount = 3000.0
'@)

# ============ Race ============
$raceUid = Get-Uid "Scripts/Data/Configs/RaceConfig.cs"
$startUid = Get-Uid "Scripts/Data/Configs/StartingEntityConfig.cs"
$startResUid = Get-Uid "Scripts/Data/ResourceStartConfig.cs"

$ext1 = '[ext_resource type="Script"'
if ($raceUid) { $ext1 += ' uid="' + $raceUid + '"' }
$ext1 += ' path="res://Scripts/Data/Configs/RaceConfig.cs" id="1"]'

$ext2 = '[ext_resource type="Script"'
if ($startUid) { $ext2 += ' uid="' + $startUid + '"' }
$ext2 += ' path="res://Scripts/Data/Configs/StartingEntityConfig.cs" id="2"]'

$ext3 = '[ext_resource type="Script"'
if ($startResUid) { $ext3 += ' uid="' + $startResUid + '"' }
$ext3 += ' path="res://Scripts/Data/ResourceStartConfig.cs" id="3"]'

$raceContent = @'
[gd_resource type="Resource" script_class="RaceConfig" load_steps=9 format=3]

'@ + $ext1 + "`n" + $ext2 + "`n" + $ext3 + @'

[sub_resource type="Resource" id="r_start"]
script = ExtResource("3")
Type = 0
Amount = 2000

[sub_resource type="Resource" id="se_command"]
script = ExtResource("2")
Kind = 1
EntityId = "CommandCenter"
TeamMode = 0
Offset = Vector2(0, 0)

[sub_resource type="Resource" id="se_scv1"]
script = ExtResource("2")
Kind = 0
EntityId = "SCV"
Offset = Vector2(-150, -150)

[sub_resource type="Resource" id="se_scv2"]
script = ExtResource("2")
Kind = 0
EntityId = "SCV"
Offset = Vector2(150, -150)

[sub_resource type="Resource" id="se_scv3"]
script = ExtResource("2")
Kind = 0
EntityId = "SCV"
Offset = Vector2(-150, 150)

[sub_resource type="Resource" id="se_scv4"]
script = ExtResource("2")
Kind = 0
EntityId = "SCV"
Offset = Vector2(150, 150)

[sub_resource type="Resource" id="se_ore1"]
script = ExtResource("2")
Kind = 3
EntityId = "IronOre"
TeamMode = 1
Offset = Vector2(400, -300)

[sub_resource type="Resource" id="se_ore2"]
script = ExtResource("2")
Kind = 3
EntityId = "IronOre"
TeamMode = 1
Offset = Vector2(400, 0)

[sub_resource type="Resource" id="se_gas"]
script = ExtResource("2")
Kind = 3
EntityId = "GasSpring"
TeamMode = 1
Offset = Vector2(400, 300)

[resource]
script = ExtResource("1")
RaceId = "Union"
DisplayName = "联合"
Offense = 3
Defense = 3
Economy = 3
Support = 2
Technology = 2
Difficulty = 2
StartingResources = Array[Resource]([SubResource("r_start")])
StartingEntities = Array[Resource]([SubResource("se_command"), SubResource("se_scv1"), SubResource("se_scv2"), SubResource("se_scv3"), SubResource("se_scv4"), SubResource("se_ore1"), SubResource("se_ore2"), SubResource("se_gas")])
AvailableUnitIds = Array[String](["SCV", "RifleMan", "Biped", "Hp"])
AvailableStructureIds = Array[String](["CommandCenter", "BB", "VF", "NanoCore", "Tower"])
'@

Save-Table "Union" $raceContent

# ============ Buffs ============
Save-Table "StimBuff" (New-Table "BuffConfig" "Scripts/Data/Configs/BuffConfig.cs" @'
BuffId = "StimBuff"
DisplayName = "兴奋剂"
Duration = 8.0
MaxStacks = 1
DamageMultiplier = 1.1
MoveSpeedMultiplier = 1.5
'@)

Save-Table "ArmorBuff" (New-Table "BuffConfig" "Scripts/Data/Configs/BuffConfig.cs" @'
BuffId = "ArmorBuff"
DisplayName = "强化装甲"
Duration = 10.0
MaxStacks = 3
ArmorBonus = 5.0
'@)

Save-Table "SlowBuff" (New-Table "BuffConfig" "Scripts/Data/Configs/BuffConfig.cs" @'
BuffId = "SlowBuff"
DisplayName = "减速"
Duration = 3.0
MaxStacks = 1
MoveSpeedMultiplier = 0.6
'@)

# ============ Nano weapons ============
Save-Table "NanoMachineGun" (New-Table "WeaponConfig" "Scripts/Data/Configs/WeaponConfig.cs" @'
WeaponId = "NanoMachineGun"
DisplayName = "机炮"
Damage = 5.0
DamageType = 0
RangeType = 1
AttackRange = 320.0
CooldownTicks = 4
UsesProjectile = false
CanTargetGround = true
CanTargetAir = true
CanTargetStructure = true
CanTargetNeutral = true
'@)

Save-Table "NanoSniperCannon" (New-Table "WeaponConfig" "Scripts/Data/Configs/WeaponConfig.cs" @'
WeaponId = "NanoSniperCannon"
DisplayName = "狙击炮"
Damage = 30.0
BonusDamageVsArmorType = 1
BonusDamage = 40.0
DamageType = 0
RangeType = 1
AttackRange = 448.0
CooldownTicks = 40
UsesProjectile = true
ProjectileSpeed = 800
HitRadius = 12
CanTargetGround = true
CanTargetStructure = true
CanTargetNeutral = true
'@)

Save-Table "NanoAAMissile" (New-Table "WeaponConfig" "Scripts/Data/Configs/WeaponConfig.cs" @'
WeaponId = "NanoAAMissile"
DisplayName = "防空飞弹"
Damage = 20.0
DamageType = 0
RangeType = 1
AttackRange = 448.0
CooldownTicks = 20
UsesProjectile = true
ProjectileSpeed = 900
HitRadius = 18
CanTargetGround = true
CanTargetAir = true
CanTargetStructure = true
CanTargetNeutral = true
'@)

Save-Table "NanoBehemothGun" (New-Table "WeaponConfig" "Scripts/Data/Configs/WeaponConfig.cs" @'
WeaponId = "NanoBehemothGun"
DisplayName = "随身机炮"
Damage = 10.0
DamageType = 0
RangeType = 1
AttackRange = 384.0
CooldownTicks = 10
UsesProjectile = false
CanTargetGround = true
CanTargetAir = true
CanTargetStructure = true
CanTargetNeutral = true
'@)

# ============ Nano units / structures ============
Save-Table "NanoBehemoth" (New-Table "UnitConfig" "Scripts/Data/Configs/UnitConfig.cs" @'
MaxHp = 2000.0
VisionRange = 768.0
ArmorType = 1
Costs = Dictionary[int, float]({
})
BuildTime = 1.0
CanMove = true
SupplyCost = 4
WeaponIds = Array[String](["NanoBehemothGun", "NanoBehemothGun", "NanoBehemothGun", "NanoBehemothGun"])
Role = 10
'@)

Save-Table "NanoTurret" (New-Table "StructureConfig" "Scripts/Data/Configs/StructureConfig.cs" @'
MaxHp = 400.0
VisionRange = 500.0
Costs = Dictionary[int, float]({
6: 150.0
})
BuildTime = 8.0
GridWidth = 2
GridHeight = 2
RequiresCreep = true
RequiredCreepType = 3
IsDefenseBuilding = true
WeaponIds = Array[String](["NanoMachineGun"])
'@)

Save-Table "NanoSniper" (New-Table "StructureConfig" "Scripts/Data/Configs/StructureConfig.cs" @'
MaxHp = 400.0
VisionRange = 500.0
Costs = Dictionary[int, float]({
6: 200.0
})
BuildTime = 8.0
GridWidth = 2
GridHeight = 2
RequiresCreep = true
RequiredCreepType = 3
IsDefenseBuilding = true
WeaponIds = Array[String](["NanoSniperCannon"])
'@)

Save-Table "NanoAA" (New-Table "StructureConfig" "Scripts/Data/Configs/StructureConfig.cs" @'
MaxHp = 300.0
VisionRange = 500.0
Costs = Dictionary[int, float]({
6: 150.0
})
BuildTime = 8.0
GridWidth = 2
GridHeight = 2
RequiresCreep = true
RequiredCreepType = 3
IsDefenseBuilding = true
WeaponIds = Array[String](["NanoAAMissile"])
'@)

Save-Table "NanoHarvester" (New-Table "StructureConfig" "Scripts/Data/Configs/StructureConfig.cs" @'
MaxHp = 600.0
VisionRange = 400.0
Costs = Dictionary[int, float]({
6: 300.0
})
BuildTime = 12.0
GridWidth = 4
GridHeight = 4
RequiresCreep = true
RequiredCreepType = 3
IsResourceBuilding = true
'@)

Save-Table "NanoActiveTower" (New-Table "StructureConfig" "Scripts/Data/Configs/StructureConfig.cs" @'
MaxHp = 400.0
VisionRange = 400.0
Costs = Dictionary[int, float]({
6: 200.0
})
BuildTime = 8.0
GridWidth = 2
GridHeight = 2
RequiresCreep = true
RequiredCreepType = 3
IsInteractiveBuilding = true
'@)

Save-Table "NanoSmokeTower" (New-Table "StructureConfig" "Scripts/Data/Configs/StructureConfig.cs" @'
MaxHp = 400.0
VisionRange = 400.0
Costs = Dictionary[int, float]({
6: 200.0
})
BuildTime = 8.0
GridWidth = 2
GridHeight = 2
RequiresCreep = true
RequiredCreepType = 3
IsInteractiveBuilding = true
'@)

# ============ Nano race ============
$nanoRaceContent = @'
[gd_resource type="Resource" script_class="RaceConfig" load_steps=6 format=3]

'@ + $ext1 + "`n" + $ext2 + "`n" + $ext3 + @'

[sub_resource type="Resource" id="nr_start"]
script = ExtResource("3")
Type = 6
Amount = 100

[sub_resource type="Resource" id="nse_behemoth"]
script = ExtResource("2")
Kind = 0
EntityId = "NanoBehemoth"
TeamMode = 0

[sub_resource type="Resource" id="nse_harvester"]
script = ExtResource("2")
Kind = 1
EntityId = "NanoHarvester"
TeamMode = 0
Offset = Vector2(300, 0)

[resource]
script = ExtResource("1")
RaceId = "Nano"
DisplayName = "纳米虫"
Offense = 1
Defense = 4
Economy = 3
Support = 2
Technology = 5
Difficulty = 1
StartingResources = Array[Resource]([SubResource("nr_start")])
StartingEntities = Array[Resource]([SubResource("nse_behemoth"), SubResource("nse_harvester")])
AvailableUnitIds = Array[String](["NanoBehemoth"])
AvailableStructureIds = Array[String](["NanoTurret", "NanoSniper", "NanoAA", "NanoHarvester", "NanoActiveTower", "NanoSmokeTower"])
'@

Save-Table "Nano" $nanoRaceContent

Write-Output "ALL_CONFIG_TABLES_GENERATED"
