namespace DnsServerCore.Dns.AntiRogue
{
    public enum RogueResult
    {
        NotTested,
        Rogue,
        NotRogue,
        CannotDetermine,
        Testing,
    }
}