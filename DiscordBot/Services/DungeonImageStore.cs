namespace DiscordBot.Services
{
    /// <summary>
    /// Holds the dashboard's latest picture, so the web host can serve it. Every new picture bumps the version,
    /// which the dashboard puts in the image URL so Discord fetches the new one after each edit.
    /// </summary>
    public class DungeonImageStore
    {
        private readonly object _lock = new object();
        private byte[] _png;

        public int Version { get; private set; }

        public void Update(byte[] png)
        {
            lock (_lock)
            {
                _png = png;
                Version++;
            }
        }

        /// <summary>Returns the latest picture, or null when none has been made yet.</summary>
        public byte[] Latest()
        {
            lock (_lock)
            {
                return _png;
            }
        }
    }
}
