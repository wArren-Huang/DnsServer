namespace DnsServerCore.Dns.AntiRogue
{
    public class WildcardDomainRecord
    {
        public string WildcardDomainName { get; }

        public WildcardDomainRecord(string domainName)
        {
            WildcardDomainName = domainName;
        }

        public bool Dominates(string subDomainName)
        {
            return WildcardDomainName.Length <= subDomainName.Length && 
                   subDomainName.Substring(subDomainName.Length - WildcardDomainName.Length).Equals(WildcardDomainName);
        }
    }
}