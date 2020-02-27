using System;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace DnsServerCore.Dns.SmartResolver
{
    public static class HttpSpeedTester
    {
        private const int DefaultPort = 80;

        public static ResponseResult Test(string ipAddress, int speedTestTimeout)
        {
            return Test(ipAddress, DefaultPort, speedTestTimeout);
        }

        public static ResponseResult Test(string ipAddress, int port, int speedTestTimeout)
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
                    Method = SpeedTestMethod.Http
                };
            }

            try
            {
                tcpClient.EndConnect(result);
                stopwatch.Stop();
                return new ResponseResult()
                {
                    IpAddress = ipAddress,
                    Time = stopwatch.ElapsedMilliseconds,
                    Result = SpeedTestResult.SuccessWithoutValidation,
                    Method = SpeedTestMethod.Http
                };
            }
            catch (SocketException)
            {
                stopwatch.Stop();
                return new ResponseResult()
                {
                    IpAddress = ipAddress,
                    Time = stopwatch.ElapsedMilliseconds,
                    Result = SpeedTestResult.Denied,
                    Method = SpeedTestMethod.Http
                };
            }
            catch (Exception e)
            {
                throw new SpeedTestException("Unhandled exception when HttpSpeedTester trying to EndConnect", e);
            }
            finally
            {
                waitHandle.Close();
                tcpClient.Close();
            }
        }
    }
}