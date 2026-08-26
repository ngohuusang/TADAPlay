using System;
using System.Collections.Generic;

namespace TadaPlay.Utils
{
    /// <summary>
    /// A host's match clock as it stands RIGHT NOW, rather than as it stood at their last
    /// capture.
    ///
    /// A host only learns its own match clock by parsing the record, and only parses it when it
    /// takes a capture - every 10 seconds while somebody is watching, every 90 when nobody is,
    /// and not at all while the game has written nothing new. So the number that reaches a
    /// viewer is a still frame, and between captures it is up to a minute and a half behind.
    ///
    /// That gap is not cosmetic. The one thing the clock is for is judging how far behind live
    /// a replay is, and a viewer watching at normal speed sees their own in-game clock walk
    /// past the host's - which cannot happen, since they started behind and both move at the
    /// same rate. The readout looked broken precisely when it was being relied on.
    ///
    /// A match clock advances with the wall clock, one second of game per second of real time,
    /// so the missing time can simply be added back: anchor on a capture when it is first seen,
    /// then count seconds off the local clock.
    ///
    /// Two details make that hold up:
    ///
    /// - The anchor is keyed to the VALUE, not to when it arrived. The lobby re-broadcasts the
    ///   whole user list whenever anybody's state changes, so the same capture arrives again
    ///   and again during one match; re-anchoring on arrival would drag the clock backwards
    ///   every time somebody else logged in.
    /// - Extrapolation is capped (see <see cref="MaxExtrapolationMs"/>). The one thing that
    ///   breaks the "one second per second" assumption is the game being PAUSED, which stops
    ///   the host's clock while the wall clock carries on. Uncapped, a three minute pause would
    ///   put the readout three minutes into a future that has not happened.
    ///
    /// A capture is always believed as it stands, even when it comes in lower than what was
    /// being extrapolated - which is what a pause looks like from here. It is measured, and
    /// the extrapolation is a guess; holding the guess would mean showing a match time the host
    /// has said it is not at, for as long as it took them to get there.
    /// </summary>
    public static class LiveMatchClock
    {
        /// <summary>
        /// How far past a capture the clock may be carried, in milliseconds.
        ///
        /// Sized just past the slowest capture cadence (90 seconds, for a match nobody is
        /// watching), so it never engages while a viewer is actually being served - captures
        /// are 10 seconds apart then, and each one re-anchors. It exists for the cases where
        /// the assumption underneath extrapolation has failed: a paused game, a host that has
        /// stopped publishing, a match that has quietly ended.
        /// </summary>
        private const long MaxExtrapolationMs = 120 * 1000;

        private static readonly object Gate = new();
        private static readonly Dictionary<string, Anchor> Anchors =
            new(StringComparer.OrdinalIgnoreCase);

        private sealed class Anchor
        {
            public long CapturedMs;   // match clock in the capture
            public long AgeMs;        // how old that capture already was when it was sent
            public DateTime SeenUtc;  // when this pair was first seen here
        }

        /// <summary>
        /// The host's match clock now, in milliseconds of game time.
        ///
        /// <paramref name="key"/> identifies the host - a username, or a VPN address where no
        /// name is to hand. It only has to be used consistently for one host; anything else
        /// simply gets its own anchor and starts counting again.
        ///
        /// Extrapolates only while the host is still playing. Once the match is over the record
        /// is all there is, and its length is the answer.
        /// </summary>
        public static long LiveMs(string key, long capturedMs, long ageMs, bool inGame)
        {
            if (capturedMs <= 0 || !inGame) return capturedMs;
            if (string.IsNullOrWhiteSpace(key)) key = "?";
            if (ageMs < 0) ageMs = 0;

            lock (Gate)
            {
                if (!Anchors.TryGetValue(key, out Anchor anchor)
                    || anchor.CapturedMs != capturedMs || anchor.AgeMs != ageMs)
                {
                    anchor = new Anchor
                    {
                        CapturedMs = capturedMs,
                        AgeMs = ageMs,
                        SeenUtc = DateTime.UtcNow
                    };
                    Anchors[key] = anchor;
                }

                double since = (DateTime.UtcNow - anchor.SeenUtc).TotalMilliseconds;
                long carried = anchor.AgeMs + (since <= 0 ? 0 : (long)since);
                if (carried > MaxExtrapolationMs) carried = MaxExtrapolationMs;
                return anchor.CapturedMs + carried;
            }
        }

        /// <summary>
        /// Drops a host's anchor. Nothing depends on this being called - a new match reports a
        /// clock low enough to re-anchor on its own - but it keeps a long-lived process from
        /// holding a row for every player it has ever seen play.
        /// </summary>
        public static void Forget(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            lock (Gate) Anchors.Remove(key);
        }
    }
}
