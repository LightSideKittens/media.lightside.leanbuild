using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace LightSide.Lean
{
    /// <summary>Opens and shuts a disclosure body by unrolling its height.</summary>
    /// <remarks>
    /// <para>
    /// A height is animated rather than a class toggled because USS transitions cannot interpolate to or
    /// from <c>auto</c>, and what a body comes to when open is whatever its contents come to. So the body
    /// is clipped while it moves and its children are held at their natural height: the contents are
    /// revealed by the clip instead of being squeezed by it, and the open height can be measured off the
    /// children while the body itself is still shut.
    /// </para>
    /// <para>
    /// 120 ms and quadratic out are the motion the rest of the LightSide editor uses, and LightSide Core
    /// owns that vocabulary. They are spelled again here because this package is installed on its own, by
    /// people who hold no LightSide licence: depending on Core would put an access token in front of a
    /// package that promises to need none, and would pull Burst, Collections and Mathematics into a
    /// project this package exists to make smaller. Keep the two in step by hand.
    /// </para>
    /// </remarks>
    internal static class Unroll
    {
        private const float DurationMs = 120f;

        /// <summary>
        /// The most motion one tick may consume. A stalled frame advances by this much instead of by its
        /// real length, so an unroll carries on from where it paused rather than jumping to the end.
        /// </summary>
        private const float MaxStepMs = 34f;

        private static readonly Dictionary<VisualElement, Motion> motions = new();

        /// <summary>
        /// Opens or shuts <paramref name="body"/>. Asking for the state it already holds, or the one a
        /// running motion is already heading for, changes nothing — an instant re-assertion never cuts an
        /// unroll short. Toggling mid-motion carries on from the height it has reached.
        /// </summary>
        internal static void Set(VisualElement body, bool expanded, bool animate)
        {
            var running = motions.TryGetValue(body, out var found) ? found : null;
            var current = running?.Expanding ?? body.style.display.value != DisplayStyle.None;
            if (current == expanded) return;

            if (!animate || body.panel == null)
            {
                Stop(body);
                body.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
                return;
            }

            var from = running?.Height ?? (expanded ? 0f : Extent(body));
            Stop(body);

            var motion = new Motion(body, expanded, float.IsNaN(from) ? 0f : from);
            motions[body] = motion;
            motion.Begin();
        }

        /// <summary>Ends whatever is running on <paramref name="body"/> and gives back the styles it held.</summary>
        private static void Stop(VisualElement body)
        {
            if (motions.Remove(body, out var motion)) motion.Release();
        }

        /// <summary>
        /// Height the children occupy, including a trailing margin that <c>layout</c> leaves out.
        /// </summary>
        /// <remarks>Measured off the children because a body clamped to a height of its own reports
        /// neither the slack nor the overflow that height hides.</remarks>
        private static float Extent(VisualElement body)
        {
            var extent = body.resolvedStyle.paddingTop + body.resolvedStyle.borderTopWidth;
            foreach (var child in body.Children())
            {
                if (child.resolvedStyle.display == DisplayStyle.None) continue;
                var bottom = child.layout.yMax + child.resolvedStyle.marginBottom;
                if (bottom > extent) extent = bottom;
            }
            return extent + body.resolvedStyle.paddingBottom + body.resolvedStyle.borderBottomWidth;
        }

        private sealed class Motion
        {
            private readonly VisualElement body;
            private readonly List<(VisualElement Child, StyleFloat Shrink)> held = new();
            private readonly StyleLength heldHeight;
            private readonly StyleLength heldMinHeight;
            private readonly StyleEnum<Overflow> heldOverflow;
            private readonly float from;

            private IVisualElementScheduledItem tick;
            private float target;
            private float progress;
            private double last;

            internal Motion(VisualElement body, bool expanded, float from)
            {
                this.body = body;
                this.from = from;
                Expanding = expanded;
                Height = from;
                heldHeight = body.style.height;
                heldMinHeight = body.style.minHeight;
                heldOverflow = body.style.overflow;
            }

            /// <summary>Which way this motion is going.</summary>
            internal bool Expanding { get; }

            /// <summary>Height the body has reached, which is where a reversal starts from.</summary>
            internal float Height { get; private set; }

            internal void Begin()
            {
                foreach (var child in body.Children())
                {
                    held.Add((child, child.style.flexShrink));
                    child.style.flexShrink = 0f;
                }
                body.style.display = DisplayStyle.Flex;
                body.style.overflow = Overflow.Hidden;
                body.style.minHeight = 0f;
                body.style.height = from;
                body.RegisterCallback<DetachFromPanelEvent>(Detached);

                if (!Expanding)
                {
                    Run(0f);
                    return;
                }
                body.RegisterCallback<GeometryChangedEvent>(Measured);
            }

            internal void Release()
            {
                tick?.Pause();
                tick = null;
                body.UnregisterCallback<GeometryChangedEvent>(Measured);
                body.UnregisterCallback<DetachFromPanelEvent>(Detached);
                body.style.height = heldHeight;
                body.style.minHeight = heldMinHeight;
                body.style.overflow = heldOverflow;
                foreach (var (child, shrink) in held) child.style.flexShrink = shrink;
                held.Clear();
            }

            /// <summary>
            /// Reads the open height once the children have been laid out, which is the first layout pass
            /// after a body that was shut is shown.
            /// </summary>
            private void Measured(GeometryChangedEvent evt)
            {
                body.UnregisterCallback<GeometryChangedEvent>(Measured);
                Run(Extent(body));
            }

            private void Run(float to)
            {
                target = to;
                progress = 0f;
                last = EditorApplication.timeSinceStartup;
                tick = body.schedule.Execute(Step).Every(0);
            }

            private void Step()
            {
                var now = EditorApplication.timeSinceStartup;
                var delta = Mathf.Min((float)((now - last) * 1000.0), MaxStepMs);
                last = now;
                progress = Mathf.Min(1f, progress + delta / DurationMs);

                var eased = 1f - (1f - progress) * (1f - progress);
                Height = Mathf.Lerp(from, target, eased);
                body.style.height = Height;
                if (progress < 1f) return;

                var expanded = Expanding;
                Stop(body);
                if (!expanded) body.style.display = DisplayStyle.None;
            }

            private void Detached(DetachFromPanelEvent evt) => Stop(body);
        }
    }
}
