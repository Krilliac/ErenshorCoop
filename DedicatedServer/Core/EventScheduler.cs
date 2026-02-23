using System;
using System.Collections.Generic;

namespace ErenshorDedicatedServer.Core
{
    /// <summary>
    /// Generic event scheduler for timed and deferred game events.
    /// Supports one-shot and repeating events with priority ordering.
    /// Used for respawn timers, buff/debuff expirations, periodic broadcasts, etc.
    /// </summary>
    public class EventScheduler
    {
        private readonly List<ScheduledEvent> _events = new();
        private readonly object _lock = new object();
        private int _nextEventId;

        /// <summary>
        /// Schedules a one-shot event to fire after a delay.
        /// </summary>
        /// <returns>Event ID for cancellation.</returns>
        public int ScheduleOnce(float delaySeconds, Action callback, string tag = null)
        {
            return Schedule(delaySeconds, 0, 1, callback, tag);
        }

        /// <summary>
        /// Schedules a repeating event.
        /// </summary>
        /// <param name="delaySeconds">Initial delay before first execution.</param>
        /// <param name="intervalSeconds">Interval between subsequent executions.</param>
        /// <param name="maxRepeats">Max number of executions. 0 = infinite.</param>
        /// <param name="callback">Action to execute.</param>
        /// <param name="tag">Optional tag for identification/cancellation.</param>
        /// <returns>Event ID for cancellation.</returns>
        public int ScheduleRepeating(float delaySeconds, float intervalSeconds, Action callback,
            int maxRepeats = 0, string tag = null)
        {
            return Schedule(delaySeconds, intervalSeconds, maxRepeats, callback, tag);
        }

        private int Schedule(float delaySeconds, float intervalSeconds, int maxRepeats,
            Action callback, string tag)
        {
            if (callback == null) return -1;

            lock (_lock)
            {
                var id = _nextEventId++;
                var evt = new ScheduledEvent
                {
                    Id = id,
                    NextFireTime = DateTime.UtcNow.AddSeconds(delaySeconds),
                    IntervalSeconds = intervalSeconds,
                    MaxRepeats = maxRepeats,
                    ExecutionCount = 0,
                    Callback = callback,
                    Tag = tag ?? "",
                    IsCancelled = false
                };

                _events.Add(evt);
                _events.Sort((a, b) => a.NextFireTime.CompareTo(b.NextFireTime));
                return id;
            }
        }

        /// <summary>
        /// Cancels a scheduled event by its ID.
        /// </summary>
        public bool Cancel(int eventId)
        {
            lock (_lock)
            {
                for (int i = 0; i < _events.Count; i++)
                {
                    if (_events[i].Id == eventId)
                    {
                        _events[i].IsCancelled = true;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Cancels all events with a specific tag.
        /// </summary>
        public int CancelByTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return 0;

            int count = 0;
            lock (_lock)
            {
                foreach (var evt in _events)
                {
                    if (evt.Tag == tag && !evt.IsCancelled)
                    {
                        evt.IsCancelled = true;
                        count++;
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// Processes ready events. Call this every tick.
        /// </summary>
        public void Tick(float deltaTime)
        {
            var now = DateTime.UtcNow;
            List<ScheduledEvent> readyEvents = null;
            List<int> toRemove = null;

            lock (_lock)
            {
                for (int i = 0; i < _events.Count; i++)
                {
                    var evt = _events[i];

                    if (evt.IsCancelled)
                    {
                        if (toRemove == null) toRemove = new List<int>();
                        toRemove.Add(i);
                        continue;
                    }

                    if (evt.NextFireTime <= now)
                    {
                        if (readyEvents == null) readyEvents = new List<ScheduledEvent>();
                        readyEvents.Add(evt);

                        evt.ExecutionCount++;

                        // Check if event should repeat
                        bool shouldRepeat = evt.IntervalSeconds > 0 &&
                                           (evt.MaxRepeats == 0 || evt.ExecutionCount < evt.MaxRepeats);

                        if (shouldRepeat)
                        {
                            evt.NextFireTime = now.AddSeconds(evt.IntervalSeconds);
                        }
                        else
                        {
                            if (toRemove == null) toRemove = new List<int>();
                            toRemove.Add(i);
                        }
                    }
                }

                // Remove finished/cancelled events (reverse order to preserve indices)
                if (toRemove != null)
                {
                    toRemove.Sort();
                    for (int i = toRemove.Count - 1; i >= 0; i--)
                    {
                        _events.RemoveAt(toRemove[i]);
                    }
                }

                // Re-sort after time changes
                if (readyEvents != null)
                {
                    _events.Sort((a, b) => a.NextFireTime.CompareTo(b.NextFireTime));
                }
            }

            // Execute callbacks outside the lock to avoid deadlocks
            if (readyEvents != null)
            {
                foreach (var evt in readyEvents)
                {
                    try
                    {
                        evt.Callback();
                    }
                    catch (Exception ex)
                    {
                        ServerLogger.Error($"Event callback error (tag: {evt.Tag}): {ex.Message}", "SCHED");
                    }
                }
            }
        }

        /// <summary>
        /// Gets the number of pending events.
        /// </summary>
        public int GetPendingCount()
        {
            lock (_lock)
            {
                int count = 0;
                foreach (var evt in _events)
                {
                    if (!evt.IsCancelled) count++;
                }
                return count;
            }
        }

        /// <summary>
        /// Clears all scheduled events.
        /// </summary>
        public void Clear()
        {
            lock (_lock) { _events.Clear(); }
        }
    }

    public class ScheduledEvent
    {
        public int Id { get; set; }
        public DateTime NextFireTime { get; set; }
        public float IntervalSeconds { get; set; }
        public int MaxRepeats { get; set; }
        public int ExecutionCount { get; set; }
        public Action Callback { get; set; }
        public string Tag { get; set; } = "";
        public bool IsCancelled { get; set; }
    }
}
