using System;

namespace DnsServerCore.Dns.AntiRogue
{
    public class UpdateStatus
    {
        public DateTime UpdatedAt { get; set; }
        public DateTime SavedAt { get; set; }

        public bool IsDirty => UpdatedAt.CompareTo(SavedAt) > 0;

        public void Update()
        {
            lock (this)
            {
                UpdatedAt = DateTime.Now;
            }
        }
    }
}