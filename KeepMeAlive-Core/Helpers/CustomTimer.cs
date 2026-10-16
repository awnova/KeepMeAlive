//====================[ Imports ]====================
using System;

namespace KeepMeAlive.Helpers
{
    //====================[ CustomTimer ]====================
    // No Unity/EFT refs. Emits events you can render elsewhere.
    public class CustomTimer
    {
        //====================[ State ]====================
        private bool isCountdown;
        private DateTime startTime;
        private DateTime targetEndTime;
        private float totalDurationSeconds;

        //====================[ Properties ]====================
        public bool IsRunning { get; private set; }

        //====================[ Events ]====================
        // OnTick: (timeSpan, formatted, progress01). Countdown progress is 0?1 over duration. Stopwatch progress is -1.
        public event Action<TimeSpan, string, float> OnTick;
        public event Action OnCompleted;

        //====================[ API ]====================
        public void StartCountdown(float seconds)
        {
            if (seconds <= 0f)
            {
                seconds = 0.001f;
            }

            isCountdown = true;
            IsRunning = true;
            totalDurationSeconds = seconds;

            startTime = DateTime.UtcNow;
            targetEndTime = startTime.AddSeconds(seconds);

            EmitTick(); // initial tick for immediate UI sync
        }

        public void Stop()
        {
            if (!IsRunning) return;
            
            IsRunning = false;
            OnCompleted?.Invoke();
        }

        // Call this once per frame by the driver (e.g., your game loop).
        public void Update()
        {
            if (!IsRunning) return;

            if (isCountdown)
            {
                var remaining = targetEndTime - DateTime.UtcNow;
                if (remaining.TotalSeconds <= 0)
                {
                    EmitTick(TimeSpan.Zero);
                    IsRunning = false;
                    OnCompleted?.Invoke();
                    return;
                }
                EmitTick(remaining);
            }
            else
            {
                var elapsed = DateTime.UtcNow - startTime;
                EmitTick(elapsed);
            }
        }

        //====================[ Helpers ]====================
        public TimeSpan GetTimeSpan()
        {
            if (!IsRunning)
            {
                return TimeSpan.Zero;
            }
            
            return isCountdown ? MaxZero(targetEndTime - DateTime.UtcNow) : (DateTime.UtcNow - startTime);
        }

        private void EmitTick() => EmitTick(GetTimeSpan());

        private void EmitTick(TimeSpan span)
        {
            float progress = -1f;
            if (isCountdown && totalDurationSeconds > 0f)
            {
                var remaining = (float)span.TotalSeconds;
                var t = 1f - (remaining / totalDurationSeconds);
                
                if (t < 0f)
                {
                    t = 0f;
                }
                if (t > 1f)
                {
                    t = 1f;
                }
                
                progress = t;
            }

            OnTick?.Invoke(span, Format(span, isCountdown), progress);
        }

        private static TimeSpan MaxZero(TimeSpan ts) => ts.TotalSeconds > 0 ? ts : TimeSpan.Zero;

        private static string Format(TimeSpan span, bool countdownMode)
        {
            if (countdownMode && span.TotalSeconds <= 0)
            {
                return "00:00:000";
            }
            
            return $"{(int)span.TotalMinutes:00}:{span.Seconds:00}:{span.Milliseconds:000}";
        }
    }
}