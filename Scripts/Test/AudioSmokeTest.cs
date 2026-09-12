using Godot;
using System;
using RTS.Core;

namespace RTS.Test;
public partial class AudioSmokeTest : Node
{
    public override void _Ready()
    {
        try
        {
            if (GameAudio.Instance == null || AudioServer.GetBusIndex("Sfx") < 0 || AudioServer.GetBusIndex("Music") < 0)
                throw new Exception("Audio manager/buses missing");
            foreach (string id in new[] { "click", "select", "command", "shot", "energy", "cannon", "blast", "music", "economy", "combat" })
            {
                bool music = id is "music" or "economy" or "combat";
                var clip = music ? GameAudio.BuildMusic(id == "music" ? "menu" : id) : GameAudio.BuildClip(id);
                var pcm = clip.Data;
                int peak = 0;
                for (int i = 0; i < pcm.Length; i += 2)
                    peak = Math.Max(peak, Math.Abs((int)(short)(pcm[i] | pcm[i + 1] << 8)));
                if (peak < 100 || peak >= short.MaxValue) throw new Exception("Silent/clipped PCM: " + id);
                if (music && (clip.LoopEnd != pcm.Length / 2 || clip.LoopMode != AudioStreamWav.LoopModeEnum.Forward))
                    throw new Exception("Invalid music loop");
                var result = clip.SaveToWav("res://tmp/audio_" + id + ".wav");
                if (result != Error.Ok) throw new Exception("Cannot export audio preview " + id);
                if (!music) { GameAudio.Instance.Play(id); GameAudio.Instance.Play(id); }
            }
            var audio = GameAudio.Instance;
            audio.UpdateMix(3);
            if (audio.MusicState != "menu") throw new Exception("Menu music not selected");
            var battle = new UserUI();
            AddChild(battle);
            audio.UpdateMix(3);
            if (audio.MusicState != "economy") throw new Exception("Match did not switch away from menu music");
            audio.NoteCombat();
            audio.UpdateMix(1);
            if (audio.MusicState != "combat") throw new Exception("Combat music not selected");
            audio.UpdateMix(12);
            if (audio.MusicState != "economy") throw new Exception("Combat music did not settle after fighting");
            battle.Free();
            audio.UpdateMix(3);
            if (audio.MusicState != "menu") throw new Exception("Returning to menu kept combat music");
            var center = GameAudio.SpatialMix(0, Vector2.Zero);
            var left = GameAudio.SpatialMix(400, new Vector2(-0.75f, 0));
            var right = GameAudio.SpatialMix(400, new Vector2(0.75f, 0));
            var far = GameAudio.SpatialMix(2000, Vector2.Zero);
            var outside = GameAudio.SpatialMix(400, new Vector2(2, 0));
            if (center.Pan != 0 || left.Pan >= 0 || right.Pan <= 0 || left.Gain != right.Gain ||
                far.Gain >= left.Gain || left.Gain >= center.Gain || outside.Gain != 0)
                throw new Exception("Spatial gain/panning regression");
            GD.Print("AUDIO_SMOKE_PASS");
            GetTree().Quit();
        }
        catch (Exception e) { GD.PrintErr("AUDIO_SMOKE_FAIL: " + e); GetTree().Quit(1); }
    }
}
