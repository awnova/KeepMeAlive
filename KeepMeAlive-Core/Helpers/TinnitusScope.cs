//====================[ Imports ]====================
using System;
using UnityEngine;

namespace KeepMeAlive.Helpers
{
    //====================[ TinnitusScope ]====================
    // Marks DoContusion() calls originating from KeepMeAlive for TinnitusCapPatch.
    // The game starts the ring ~0.1s AFTER DoContusion returns (Contusion has a 0.1s start delay and
    // Player.OnHealthEffectAdded fires on effect start), so a call-stack flag can't work. Instead each
    // Run() arms a single-use token that the next StartTinnitusEffect consumes, expiring after ArmSeconds.
    internal static class TinnitusScope
    {
        private const float ArmSeconds = 1f; // >> 0.1s start delay, << any realistic vanilla contusion gap
        private static float _armedUntil;

        // Wrap a single DoContusion(...) call: KeepMeAlive.Helpers.TinnitusScope.Run(() => hc.DoContusion(dur, 1f));
        public static void Run(Action doContusionCall)
        {
            _armedUntil = Time.unscaledTime + ArmSeconds;
            doContusionCall();
        }

        // True once per Run(); called by the patch from StartTinnitusEffect.
        public static bool TryConsume()
        {
            if (Time.unscaledTime > _armedUntil) return false;
            _armedUntil = 0f;
            return true;
        }

        public static void Clear() => _armedUntil = 0f;
    }
}
