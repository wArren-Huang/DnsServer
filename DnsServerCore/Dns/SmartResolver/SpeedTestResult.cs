namespace DnsServerCore.Dns.SmartResolver
{
    public enum SpeedTestResult
    {
        Timeout,
        ValidationFailed,
        SuccessWithValidation,
        SuccessWithoutValidation,
        Denied,
        Failed,
    }
}