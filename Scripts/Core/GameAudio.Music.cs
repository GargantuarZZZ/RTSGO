using Godot;
using System;

namespace RTS.Core;
public partial class GameAudio
{
    internal static AudioStreamWav BuildMusic(string mode = "menu")
    {
        if (mode == "menu") return BuildMenuMusic();
        if (mode is not ("economy" or "combat")) throw new ArgumentException("Unknown music state", nameof(mode));
        const int rate = 22050, seconds = 32;
        var data = new byte[rate * seconds * 2];
        int[] roots = { 45, 41, 48, 43, 45, 48, 41, 43 };
        int[] melody = { 12, 19, 15, 22, 19, 15, 10, 12 };
        var rng = new Random(917);
        double low = 0;
        bool combat = mode == "combat";
        for (int i = 0; i < data.Length / 2; i++)
        {
            double t = (double)i / rate;
            int bar = (int)(t / 4), step = (int)(t * 2);
            double beat = t % 0.5, local = t % 4;
            double root = 440 * Math.Pow(2, (roots[bar] - 69) / 12.0);
            double padEnv = Math.Sin(Math.PI * local / 4);
            double pad = (Math.Sin(Math.Tau * root * t) + Math.Sin(Math.Tau * root * 1.5 * t)) * 0.1 * padEnv;
            double hz = root * Math.Pow(2, melody[step % 8] / 12.0);
            double pluckEnv = Math.Min(1, beat / 0.008) * Math.Exp(-beat * 10);
            double pluck = (Math.Sin(Math.Tau * hz * t) + 0.2 * Math.Sin(Math.Tau * hz * 3 * t)) * pluckEnv * 0.14;
            double noise = rng.NextDouble() * 2 - 1;
            low = low * 0.9 + noise * 0.1;
            double rhythm = 0;
            if (combat)
            {
                double kick = Math.Sin(Math.Tau * (48 * beat + 3 * (1 - Math.Exp(-beat * 24)))) * Math.Exp(-beat * 16);
                double snare = step % 2 == 1 ? (noise - low) * Math.Exp(-beat * 24) * 0.16 : 0;
                double hat = (noise - low) * Math.Exp(-(t % 0.25) * 90) * 0.04;
                double bass = (Math.Sin(Math.Tau * root * 0.5 * t) + Math.Sin(Math.Tau * root * t) * 0.25) * Math.Exp(-beat * 4);
                rhythm = kick * 0.18 + snare + hat + bass * 0.12;
            }
            // Silence at the exact loop boundary prevents clicks; identical timing keeps layers in phase.
            double fade = Math.Min(1, Math.Min(t, seconds - t) / 0.03);
            short pcm = (short)(Math.Clamp((pad + pluck + rhythm) * fade, -0.9, 0.9) * short.MaxValue);
            data[i * 2] = (byte)(pcm & 255); data[i * 2 + 1] = (byte)(pcm >> 8);
        }
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Data = data,
            LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopBegin = 0, LoopEnd = data.Length / 2 };
    }
}
