namespace DnsServerCore.Dns.AntiRogue
{
    public class WildcardDomainRecord
    {
        public string WildcardDomainName { get; }

        public WildcardDomainRecord(string domainName)
        {
            if (domainName[0] == '.')
            {
                WildcardDomainName = domainName;
            }

            WildcardDomainName = $".{domainName}";
        }

        public bool Dominates(string subDomainName)
        {
            return subDomainName.EndsWith(WildcardDomainName);
        }
    }
}