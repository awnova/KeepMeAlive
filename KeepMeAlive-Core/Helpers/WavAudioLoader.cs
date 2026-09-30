//====================[ Imports ]====================
using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace KeepMeAlive.Helpers
{
    //====================[ WavAudioLoader ]====================
    // Parses a plain PCM .wav embedded resource into a Unity AudioClip at runtime.
    // No AssetBundle/Unity-Editor build step is required - the game's engine build
    // doesn't ship UnityWebRequestAudioModule, so this manual RIFF parse is the only
    // dependency-free way to turn a shipped .wav into a playable AudioClip here.
    internal static class WavAudioLoader
    {
        //====================[ Public API ]====================
        public static AudioClip LoadFromEmbeddedResource(string resourceName, string clipName)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    Plugin.LogSource.LogError($"[WavAudioLoader] Embedded resource '{resourceName}' not found.");
                    return null;
                }

                byte[] bytes;
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    bytes = ms.ToArray();
                }

                return ParseWav(bytes, clipName);
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogError($"[WavAudioLoader] LoadFromEmbeddedResource('{resourceName}') error: {ex}");
                return null;
            }
        }

        //====================[ Parsing ]====================
        private static AudioClip ParseWav(byte[] data, string clipName)
        {
            if (data == null || data.Length < 44)
            {
                Plugin.LogSource.LogError("[WavAudioLoader] WAV data too small to contain a valid header.");
                return null;
            }

            if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F' ||
                data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E')
            {
                Plugin.LogSource.LogError("[WavAudioLoader] Not a valid RIFF/WAVE file.");
                return null;
            }

            int channels = 0;
            int sampleRate = 0;
            int bitsPerSample = 0;
            int dataOffset = -1;
            int dataLength = 0;

            int pos = 12;
            while (pos + 8 <= data.Length)
            {
                string chunkId = System.Text.Encoding.ASCII.GetString(data, pos, 4);
                int chunkSize = BitConverter.ToInt32(data, pos + 4);
                int chunkDataStart = pos + 8;

                if (chunkId == "fmt ")
                {
                    channels = BitConverter.ToInt16(data, chunkDataStart + 2);
                    sampleRate = BitConverter.ToInt32(data, chunkDataStart + 4);
                    bitsPerSample = BitConverter.ToInt16(data, chunkDataStart + 14);
                }
                else if (chunkId == "data")
                {
                    dataOffset = chunkDataStart;
                    dataLength = chunkSize;
                }

                // Chunks are word-aligned; padding byte follows odd-sized chunks.
                pos = chunkDataStart + chunkSize + (chunkSize % 2);
            }

            if (dataOffset < 0 || channels <= 0 || sampleRate <= 0 || bitsPerSample != 16)
            {
                Plugin.LogSource.LogError(
                    $"[WavAudioLoader] Unsupported or malformed WAV (channels={channels}, sampleRate={sampleRate}, bitsPerSample={bitsPerSample}). Only 16-bit PCM is supported.");
                return null;
            }

            if (dataOffset + dataLength > data.Length)
                dataLength = data.Length - dataOffset;

            int sampleCount = dataLength / 2; // 16-bit = 2 bytes per sample
            int frameCount = sampleCount / channels;
            var samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                short raw = BitConverter.ToInt16(data, dataOffset + i * 2);
                samples[i] = raw / 32768f;
            }

            var clip = AudioClip.Create(clipName, frameCount, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
