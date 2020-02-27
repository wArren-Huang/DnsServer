using System;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace DnsServerCore.Dns.SmartResolver
{
    public static class HttpsSpeedTester
    {
        private const int DefaultPort = 443;

        public static ResponseResult Test(string ipAddress, string domain, int speedTestTimeout)
        {
            return Test(ipAddress, DefaultPort, domain, speedTestTimeout);
        }

        public static ResponseResult Test(string ipAddress, int port, string domain, int speedTestTimeout)
        {
            var stopwatch = new Stopwatch();
            var tcpClient = new TcpClient();
            
            stopwatch.Start();
            var result = tcpClient.BeginConnect(ipAddress, port, null, null);
            var waitHandle = result.AsyncWaitHandle;
            var success = waitHandle.WaitOne(TimeSpan.FromMilliseconds(speedTestTimeout), false);

            if (!success)
            {
                tcpClient.Close();
                stopwatch.Stop();
                return new ResponseResult()
                {
                    IpAddress = ipAddress, 
                    Time = stopwatch.ElapsedMilliseconds, 
                    Result = SpeedTestResult.Timeout,
                    Method = SpeedTestMethod.Https
                };
            }

            try
            {
                tcpClient.EndConnect(result);
            }
            catch (SocketException)
            {
                stopwatch.Stop();
                return new ResponseResult()
                {
                    IpAddress = ipAddress,
                    Time = stopwatch.ElapsedMilliseconds,
                    Result = SpeedTestResult.Denied,
                    Method = SpeedTestMethod.Https
                };
            }
            catch (Exception e)
            {
                throw new SpeedTestException("Unhandled exception when HttpsSpeedTester trying to EndConnect", e);
            }
            try
            { 
                var sslStream = new SslStream(
                    tcpClient.GetStream(), 
                    false, 
                    new RemoteCertificateValidationCallback(
                        (o, certificate, chain, errors) => errors == SslPolicyErrors.None), 
                    null);
                
                sslStream.AuthenticateAsClient(domain);
                
                stopwatch.Stop();
                return new ResponseResult()
                {
                    IpAddress = ipAddress, 
                    Time = stopwatch.ElapsedMilliseconds, 
                    Result = SpeedTestResult.SuccessWithValidation,
                    Method = SpeedTestMethod.Https
                };
            }
            catch (AuthenticationException)
            {
                stopwatch.Stop();
                return new ResponseResult()
                {
                    IpAddress = ipAddress, 
                    Time = stopwatch.ElapsedMilliseconds, 
                    Result = SpeedTestResult.ValidationFailed,
                    Method = SpeedTestMethod.Https
                };
            }
            finally
            {
                tcpClient.Close();
            }
        }
    }
}