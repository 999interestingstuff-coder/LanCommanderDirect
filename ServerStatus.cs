using System;

namespace LanCommanderDirect
{
    /// <summary>
    /// The server could not be reached at all (nothing listening, no network, no answer in time). A wrong password or an
    /// error answer from the server is NOT this: the server answered, so it is online.
    /// </summary>
    public class LcServerUnreachableException : Exception
    {
        public LcServerUnreachableException(string serverUrl, string reason, Exception inner)
            : base("Can't reach the LANCommander server at " + serverUrl + ": " + reason, inner)
        {
        }
    }

    /// <summary>Remembers whether the last contact with the server worked, so popups only appear when that changes.</summary>
    static class LcServerStatus
    {
        static readonly object gate = new object();
        static bool offline;

        public static bool Offline { get { lock (gate) return offline; } }

        /// <summary>Marks the server reachable. True if it was marked offline before.</summary>
        public static bool SetOnline()
        {
            lock (gate)
            {
                var was = offline;
                offline = false;
                return was;
            }
        }

        /// <summary>Marks the server unreachable. True if it was not marked offline before.</summary>
        public static bool SetOffline()
        {
            lock (gate)
            {
                var changed = !offline;
                offline = true;
                return changed;
            }
        }
    }
}
