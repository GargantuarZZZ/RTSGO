using Godot;
using System;
using System.Collections.Generic;
using RTS.Core.Events;

namespace RTS.Core;

// Presentation only: synthesized PCM, cached once; never uses simulation RNG.
public partial class GameAudio : Node
{
    public static GameAudio Instance { get; private set; }
    private readonly Dictionary<string, AudioStreamWav> _clips = new();
    private readonly Dictionary<string, ulong> _last = new();
    private readonly List<AudioStreamPlayer> _voices = new();
    private readonly Dictionary<int, AudioEffectPanner> _panners = new();
    private readonly Dictionary<int, Vector3> _voicePositions = new();
    private readonly Dictionary<string, AudioStreamPlayer> _music = new();
    private readonly HashSet<ulong> _battleViews = new();
    private double _combatHold;
    private double _duck;
    private int _soundVariation;
    public string MusicState { get; private set; } = "menu";

    public override void _Ready()
    {
        Instance = this;
        RTS.Settings.GameSettings.Load();
        RTS.Settings.GameSettings.ApplyAudio();
        foreach (string id in new[] { "click", "select", "command", "shot", "energy", "cannon", "blast" }) _clips[id] = BuildClip(id);
        for (int i = 0; i < 10; i++)
        {
            var voice = new AudioStreamPlayer { Bus = "Sfx", VolumeDb = -14 };
            if (i >= 2)
            {
                string busName = "RTS_Spatial_" + i;
                int bus = AudioServer.GetBusIndex(busName);
                if (bus < 0)
                {
                    AudioServer.AddBus(); bus = AudioServer.BusCount - 1;
                    AudioServer.SetBusName(bus, busName);
                    AudioServer.SetBusSend(bus, "Sfx");
                    AudioServer.AddBusEffect(bus, new AudioEffectPanner());
                }
                _panners[i] = (AudioEffectPanner)AudioServer.GetBusEffect(bus, 0);
                voice.Bus = busName;
            }
            AddChild(voice); _voices.Add(voice);
        }
        foreach (string state in new[] { "menu", "economy", "combat" })
        {
            var music = new AudioStreamPlayer { Name = "Music_" + state, Bus = "Music", VolumeLinear = 0, Stream = BuildMusic(state) };
            AddChild(music); _music[state] = music;
            music.Play();
        }
        GetTree().NodeAdded += OnNodeAdded;
        GetTree().NodeRemoved += OnNodeRemoved;
        GameEventBus.SelectionChanged += OnSelection;
        HookTree(GetTree().Root);
    }
    private void HookTree(Node node)
    {
        OnNodeAdded(node);
        foreach (Node child in node.GetChildren()) HookTree(child);
    }
    private void OnNodeAdded(Node node)
    {
        if (node is UserUI) _battleViews.Add(node.GetInstanceId());
        if (node is BaseButton button && !button.HasMeta("audio_hooked"))
        {
            button.SetMeta("audio_hooked", true);
            button.Pressed += () => Play("click");
        }
    }
    private void OnNodeRemoved(Node node)
    {
        if (node is UserUI) { _battleViews.Remove(node.GetInstanceId()); if (_battleViews.Count == 0) _combatHold = 0; }
    }
    public override void _Process(double delta)
    {
        UpdateMix(delta);
        var camera = GetViewport().GetCamera3D();
        if (camera == null) return;
        foreach (var pair in _voicePositions)
        {
            if (!_voices[pair.Key].Playing) continue;
            var mix = CameraMix(camera, pair.Value);
            _voices[pair.Key].VolumeDb = -20 + Mathf.LinearToDb(Mathf.Max(mix.Gain, 0.001f));
            _panners[pair.Key].Pan = mix.Pan;
        }
    }
    internal void UpdateMix(double delta)
    {
        _combatHold = Math.Max(0, _combatHold - delta);
        _duck = Math.Max(0, _duck - delta);
        MusicState = _battleViews.Count == 0 ? "menu" : _combatHold > 0 ? "combat" : "economy";
        foreach (var pair in _music)
        {
            float target = pair.Key == MusicState ? (_duck > 0 ? 0.07f : 0.13f) : 0;
            pair.Value.VolumeLinear = Mathf.MoveToward(pair.Value.VolumeLinear, target, (float)delta * (_duck > 0 ? 0.45f : 0.055f));
        }
    }
    internal void NoteCombat() { if (_battleViews.Count > 0) _combatHold = 10; }
    public void PlayWeapon(RTS.Units.Weapon weapon, Vector3 position)
    {
        string id = weapon.HasAreaDamage ? "cannon" : weapon.DmgType == RTS.Data.DamageType.EM ? "energy" : "shot";
        PlayAt(id, position);
    }
    private void OnSelection(List<RTS.Data.IEntity> selection, int team)
    { if (selection.Count > 0) Play("select"); }

    public void Play(string id)
        => PlayMixed(id, 1f, 0f);
    private void PlayMixed(string id, float gain, float pan, Vector3? position = null)
    {
        if (!_clips.TryGetValue(id, out var clip)) return;
        ulong now = Time.GetTicksMsec();
        ulong interval = id is "shot" or "energy" ? 110UL : id is "blast" or "cannon" ? 240UL : id is "select" or "command" ? 220UL : 65UL;
        bool combat = id is "shot" or "energy" or "cannon" or "blast";
        string throttleKey = combat ? id + ":" + Mathf.RoundToInt((pan + 1f) * 2f) : id;
        if (_last.TryGetValue(throttleKey, out var last) && now - last < interval) return;
        // UI has two reserved channels so battles never swallow input feedback.
        int start = combat ? 2 : 0, end = combat ? _voices.Count : 2;
        for (int i = start; i < end; i++)
            if (!_voices[i].Playing)
            {
                _last[throttleKey] = now;
                if (!combat) _duck = 0.4;
                _voices[i].Stream = clip;
                _voices[i].VolumeDb = (combat ? -20 : -12) + Mathf.LinearToDb(Mathf.Max(gain, 0.001f));
                if (_panners.TryGetValue(i, out var panner)) panner.Pan = pan;
                if (position.HasValue) _voicePositions[i] = position.Value;
                else _voicePositions.Remove(i);
                _voices[i].PitchScale = combat ? 0.96f + (_soundVariation++ % 3) * 0.04f : 1f;
                _voices[i].Play();
                return;
            }
    }
    public void PlayAt(string id, Vector3 position)
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null || camera.IsPositionBehind(position)) return;
        var mix = CameraMix(camera, position);
        if (mix.Gain <= 0.005f) return;
        var fog = RTS.World.FogOfWar.Instance;
        var world = SimManager.Instance?.World;
        if (fog != null && !fog.RevealAll && world != null)
        {
            var point = new RTS.Simulation.FPVector2((FixMath.NET.Fix64)position.X, (FixMath.NET.Fix64)position.Z);
            lock (world.SyncRoot)
                if (!world.IsInTeamVision(Main.Instance?.LocalPlayerID ?? 0, point, FixMath.NET.Fix64.Zero)) return;
        }
        NoteCombat();
        PlayMixed(id, mix.Gain, mix.Pan, position);
    }

    private (float Gain, float Pan) CameraMix(Camera3D camera, Vector3 position)
    {
        Vector2 size = GetViewport().GetVisibleRect().Size;
        if (size.X <= 0 || size.Y <= 0 || camera.IsPositionBehind(position)) return (0, 0);
        Vector2 normalized = (camera.UnprojectPosition(position) - size * 0.5f) / (size * 0.5f);
        // RTS listener is at the ground under the camera, not thousands of units above it.
        Vector3 origin = camera.ProjectRayOrigin(size * 0.5f);
        Vector3 ray = camera.ProjectRayNormal(size * 0.5f);
        Vector3 listener = Math.Abs(ray.Y) > 0.001f ? origin + ray * (-origin.Y / ray.Y) : origin;
        float distance = new Vector2(position.X - listener.X, position.Z - listener.Z).Length();
        return SpatialMix(distance, normalized);
    }

    internal static (float Gain, float Pan) SpatialMix(float distance, Vector2 screen)
    {
        float edge = Mathf.Clamp(2f - Mathf.Max(Mathf.Abs(screen.X), Mathf.Abs(screen.Y)), 0f, 1f);
        float gain = edge / Mathf.Pow(1f + Mathf.Max(0, distance) / 1400f, 2);
        return (gain, Mathf.Clamp(screen.X, -0.95f, 0.95f));
    }

    internal static AudioStreamWav BuildClip(string id)
    {
        const int rate = 22050;
        double duration = id is "blast" or "cannon" ? 0.48 : id == "energy" ? 0.22 : id == "shot" ? 0.11 : id == "command" ? 0.16 : 0.07;
        var data = new byte[(int)(duration * rate) * 2];
        var rng = new Random(701);
        double phase = 0, lowNoise = 0;
        for (int i = 0; i < data.Length / 2; i++)
        {
            double t = (double)i / rate, u = t / duration;
            double frequency = id switch { "click" => 1100 - 600 * u, "select" => 650 + 500 * u,
                "command" => u < 0.45 ? 740 : 990, "energy" => 1300 * Math.Exp(-u * 3), "shot" => 180 - 100 * u, _ => 85 - 55 * u };
            phase += Math.Tau * frequency / rate;
            double noise = rng.NextDouble() * 2 - 1;
            lowNoise = lowNoise * 0.75 + noise * 0.25;
            double signal = id is "blast" or "cannon" ? lowNoise * 1.8 + Math.Sin(phase) * 0.3 :
                id == "energy" ? Math.Sin(phase + 1.8 * Math.Sin(phase * 0.5)) * 0.55 :
                id == "shot" ? noise * 0.55 + Math.Sin(phase) * 0.3 : Math.Sin(phase) * 0.65;
            double envelope = Math.Min(1, t / 0.003) * Math.Pow(1 - u, id == "blast" ? 2 : 3);
            short sample = (short)(Math.Clamp(signal * envelope, -0.95, 0.95) * short.MaxValue);
            data[i * 2] = (byte)(sample & 255); data[i * 2 + 1] = (byte)(sample >> 8);
        }
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Stereo = false, Data = data };
    }
    private static AudioStreamWav BuildMenuMusic()
    {
        const int rate = 22050, seconds = 16;
        var data = new byte[rate * seconds * 2];
        // Four suspended chords with a soft pulse. All oscillators are original synthesis.
        int[,] notes = { { 45, 52, 59 }, { 41, 48, 55 }, { 48, 55, 62 }, { 43, 50, 57 } };
        for (int i = 0; i < rate * seconds; i++)
        {
            double t = (double)i / rate, local = t % 4;
            int chord = (int)(t / 4);
            double envelope = Math.Sin(Math.PI * local / 4);
            double sample = 0;
            for (int n = 0; n < 3; n++)
            {
                double hz = 440 * Math.Pow(2, (notes[chord, n] - 69) / 12.0);
                sample += Math.Sin(Math.Tau * hz * t) * 0.16 + Math.Sin(Math.Tau * hz * 2 * t) * 0.035;
            }
            double pulse = Math.Exp(-(t % 0.5) * 14) * Math.Sin(Math.Tau * 440 * t) * 0.05;
            short pcm = (short)((sample + pulse) * envelope * short.MaxValue);
            data[i * 2] = (byte)(pcm & 255); data[i * 2 + 1] = (byte)(pcm >> 8);
        }
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Data = data,
            LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopBegin = 0, LoopEnd = rate * seconds };
    }
    public override void _ExitTree()
    {
        GetTree().NodeAdded -= OnNodeAdded;
        GetTree().NodeRemoved -= OnNodeRemoved;
        GameEventBus.SelectionChanged -= OnSelection;
        Instance = null;
    }
}
