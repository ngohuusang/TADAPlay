using System;
using TadaPlay.Logger;
using TadaPlay.Websockets.Interface;

namespace TadaPlay.Utils
{
    /// <summary>
    /// Tells the lobby server what this machine can currently show other players.
    ///
    /// The alternative - and what this replaces - is every viewer asking every host directly
    /// over the VPN every few seconds. That is one request per player per poll, across the one
    /// link that has been dropping for half a minute at a time; during those drops a player
    /// who is perfectly fine appears unreachable to everyone. Pushed here instead, the status
    /// rides the lobby socket the app already holds and reaches everyone the moment it changes.
    ///
    /// Only this summary goes to the server. The recorded game is still fetched straight from
    /// the host over the VPN - it is megabytes and grows for the whole match, so putting it
    /// through one shared PHP process would be a poor trade.
    /// </summary>
    public static class MatchStatusPublisher
    {
        private static readonly object Gate = new();
        private static string _lastSent;

        /// <summary>
        /// Publishes the current state, unless it is identical to what was last sent.
        ///
        /// Called on every capture, so without the comparison a long match would broadcast a
        /// user-list update to every client every ten seconds for no change at all. The clock
        /// does move each capture, so this mostly suppresses the repeats around match start
        /// and end rather than the captures themselves.
        /// </summary>
        public static void Publish(IWebSocketService socket, bool allowSpectate, bool force = false)
        {
            if (socket == null) return;

            bool inGame = MatchShareState.InGame;
            string currentMatch = MatchShareState.CurrentRecordPath;
            bool hasMatch = currentMatch != null
                ? LiveRecordSnapshotStore.FindFor(currentMatch) != null
                : LiveRecordSnapshotStore.Current() != null;
            long gameMs = MatchShareState.DurationMs;
            bool paused = MatchShareState.Paused;
            int waitSeconds = MatchShareState.WaitSeconds;

            // Paused belongs in the signature: while a game is paused the clock stops moving,
            // so gameMs alone stops changing too and the pause would never be broadcast - the
            // one transition viewers most need is exactly the one that suppresses itself.
            // allowSpectate belongs in the signature: flipping the setting changes nothing
            // else about the payload, so without it the change would never be broadcast and
            // other players would keep seeing the old answer.
            string signature = $"{inGame}|{hasMatch}|{gameMs}|{paused}|{allowSpectate}";
            lock (Gate)
            {
                if (!force && signature == _lastSent) return;
                _lastSent = signature;
            }

            try
            {
                _ = socket.SendMessageAsync(new
                {
                    command = "match_status",
                    in_game = inGame,
                    has_match = hasMatch,
                    game_ms = gameMs,
                    paused = paused,
                    // Lets other clients say "playing, not spectatable" instead of showing a
                    // countdown to a capture that is never coming.
                    allow_spectate = allowSpectate,
                    wait_seconds = waitSeconds
                });
                DebugLogger.Info($"MatchStatusPublisher: sent inGame={inGame} hasMatch={hasMatch} " +
                                 $"gameMs={gameMs} paused={paused} wait={waitSeconds}s.");
            }
            catch (Exception ex)
            {
                // Never fatal: viewers fall back to asking this machine directly, which is
                // exactly how it worked before any of this existed.
                DebugLogger.Warn($"MatchStatusPublisher: cannot publish match status: {ex.Message}");
            }
        }

        /// <summary>
        /// Forgets what was last sent, so the next publish always goes out.
        ///
        /// Needed after a reconnect: the server drops this machine's match status when the
        /// connection closes, so the state it holds and the state this class believes it sent
        /// have diverged, and suppressing "the same" value would leave the server showing
        /// nothing for the rest of the match.
        /// </summary>
        public static void Reset()
        {
            lock (Gate) _lastSent = null;
        }
    }
}
