using System;
using System.Diagnostics;
using System.Net.NetworkInformation;

namespace DnsServerCore.Dns.SmartResolver
{
    public class PingSpeedTester
    {
        public static ResponseResult Test(string ipAddress, int speedTestTimeout)
        {
            var pingSender = new Ping();
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            PingReply pingReply = null;
            try
            {
                pingReply = pingSender.Send(ipAddress, speedTestTimeout);
            }
            catch (Exception e)
            {
                throw new SpeedTestException("Unhandled exception when PingSpeedTester trying to get ping reply", e);
            }
            stopwatch.Stop();
            pingSender.Dispose();
            if (pingReply == null)
            {
                return new ResponseResult()
                {
                    IpAddress = ipAddress,
                    Time = stopwatch.ElapsedMilliseconds,
                    Result = SpeedTestResult.Failed,
                    Method = SpeedTestMethod.Ping
                };
            }
            switch (pingReply.Status)
            {
                case IPStatus.Success:
                    return new ResponseResult()
                    {
                        IpAddress = ipAddress, 
                        Time = pingReply.RoundtripTime, 
                        Result = SpeedTestResult.SuccessWithoutValidation,
                        Method = SpeedTestMethod.Ping
                    };
                case IPStatus.TimedOut:
                    return new ResponseResult()
                    {
                        IpAddress = ipAddress, 
                        Time = pingReply.RoundtripTime, 
                        Result = SpeedTestResult.Timeout,
                        Method = SpeedTestMethod.Ping
                    };
                default:
                    return new ResponseResult()
                    {
                        IpAddress = ipAddress, 
                        Time = pingReply.RoundtripTime, 
                        Result = SpeedTestResult.Failed,
                        Method = SpeedTestMethod.Ping
                    };
            }
        }
    }
}