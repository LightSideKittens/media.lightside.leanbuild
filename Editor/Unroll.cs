using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace LightSide.Lean
{
    /// <summary>Opens and shuts a disclosure body by unrolling its height.</summary>
    /// <remarks>
    /// <para>
    /// A height is animated rather than a class toggled because USS transitions cannot interpolate to or
    /// from <c>auto</c>, and what a body comes to when open is whatever its contents come to. The body is
    /// clipped while it moves and its children are held at their natural height, so the contents are
    /// revealed by the clip instead of being squeezed by it.
    /// </para>
    /// <para>
    /// The open height is read in the caller's own frame: the body is shown at its natural height, the
    /// panel's layout is completed on the spot, the height is read, and only then is the body clamped
    /// shut to unroll from. Waiting for a layout event instead would leave the body sitting shut for a
    /// frame or two after the click, which reads as a stutter rather than as motion.
    /// </para>
    /// <para>
    /// 120 ms and quadratic out are the motion the rest of the LightSide editor uses, and LightSide Core
    /// owns that vocabulary. It is spelled again here because this package is installed on its own, by
    /// people who hold no LightSide licence: depending on Core would put an access token in front of a
    /// package that promises to need none, and would pull Burst, Collections and Mathematics into a
    /// project this package exists to make smaller. Keep the two in step by hand.
    /// </para>
    /// </remarks>
    internal static class Unroll
    {
        private const int DurationMs = 120;

        /// <summary>
        /// The most motion one tick may consume. A stalled frame advances by this much instead of by its
        /// real length, so an unroll carries on from where it paused rather than jumping to the end.
        /// </summary>
        private const int MaxStepMs = 34;

        private const string ClipClass = "lean__clip";

        private static readonly Dictionary<VisualElement, Motion> motions = new();
        private static MethodInfo validateLayout;
        private static bool bound;

        /// <summary>
        /// Opens or shuts <paramref name="body"/>. Asking for the state it already holds, or the one a
        /// running motion is already heading for, changes nothing — an instant re-assertion never cuts an
        /// unroll short. Toggling mid-motion carries on from the height it has reached.
        /// </summary>
        internal static void Set(VisualElement body, bool expanded, bool animate)
        {
            body.AddToClassList(ClipClass);
            var hasMotion = motions.TryGetValue(body, out var running);
            var current = hasMotion ? running.Expanding : body.style.display.value != DisplayStyle.None;
            if (current == expanded) return;

            if (!animate || body.panel == null || !Measurable(body.panel))
            {
                Stop(body);
                body.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
                return;
            }

            var start = hasMotion ? running.Height : expanded ? 0f : body.layout.height;
            if (float.IsNaN(start)) start = 0f;
            Stop(body);

            var motion = new Motion(body, expanded, start);
            foreach (var child in body.Children())
            {
                motion.Held.Add((child, child.style.flexShrink));
                child.style.flexShrink = 0f;
            }

            body.style.display = DisplayStyle.Flex;
            if (expanded)
            {
                CompleteLayout(body.panel);
                motion.Target = body.layout.height;
            }
            body.style.minHeight = 0f;
            body.style.height = start;
            body.RegisterCallback<DetachFromPanelEvent>(Detached);

            motions[body] = motion;
            motion.Play();
        }

        /// <summary>Ends whatever is running on <paramref name="body"/> and gives back the styles it held.</summary>
        private static void Stop(VisualElement body)
        {
            if (motions.Remove(body, out var motion)) motion.Release();
        }

        private static void Detached(DetachFromPanelEvent evt) => Stop((VisualElement)evt.target);

        /// <summary>Runs the panel's style and layout passes immediately, without painting.</summary>
        private static void CompleteLayout(IPanel panel) => validateLayout.Invoke(panel, null);

        /// <summary>
        /// Whether this editor lets a layout pass be completed on demand, which is what reading an open
        /// height in the caller's own frame needs. An editor that does not folds instantly.
        /// </summary>
        private static bool Measurable(IPanel panel)
        {
            if (bound) return validateLayout != null;
            bound = true;
            validateLayout = panel.GetType()
                .GetMethod("ValidateLayout", BindingFlags.Instance | BindingFlags.Public);
            return validateLayout != null;
        }

        private sealed class Motion
        {
            internal readonly List<(VisualElement Child, StyleFloat Shrink)> Held = new();

            private readonly VisualElement body;
            private readonly StyleLength heldHeight;
            private readonly StyleLength heldMinHeight;
            private readonly float start;

            private IVisualElementScheduledItem tick;
            private float progress;
            private double last;

            internal Motion(VisualElement body, bool expanded, float start)
            {
                this.body = body;
                this.start = start;
                Expanding = expanded;
                Height = start;
                heldHeight = body.style.height;
                heldMinHeight = body.style.minHeight;
            }

            /// <summary>Which way this motion is going.</summary>
            internal bool Expanding { get; }

            /// <summary>Height the body has reached, which is where a reversal starts from.</summary>
            internal float Height { get; private set; }

            /// <summary>Height the body is heading for.</summary>
            internal float Target { get; set; }

            /// <summary>Starts the motion and applies its first frame in the caller's own frame.</summary>
            internal void Play()
            {
                progress = 0f;
                last = EditorApplication.timeSinceStartup;
                tick = body.schedule.Execute(Step).Every(0);
                Apply(0f);
            }

            internal void Release()
            {
                tick?.Pause();
                tick = null;
                body.UnregisterCallback<DetachFromPanelEvent>(Detached);
                body.style.height = heldHeight;
                body.style.minHeight = heldMinHeight;
                foreach (var (child, shrink) in Held) child.style.flexShrink = shrink;
                Held.Clear();
            }

            private void Step()
            {
                if (!motions.TryGetValue(body, out var current) || current != this) return;

                var now = EditorApplication.timeSinceStartup;
                var delta = Mathf.Min((float)((now - last) * 1000.0), MaxStepMs);
                last = now;
                progress = Mathf.Min(1f, progress + delta / DurationMs);

                var eased = 1f - (1f - progress) * (1f - progress);
                Apply(eased);
                if (eased < 1f) return;

                var expanded = Expanding;
                Stop(body);
                if (!expanded) body.style.display = DisplayStyle.None;
            }

            private void Apply(float eased)
            {
                Height = Mathf.Lerp(start, Target, eased);
                body.style.height = Height;
            }
        }
    }
}
