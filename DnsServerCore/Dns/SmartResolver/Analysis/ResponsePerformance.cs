using System.Text;
using TechnitiumLibrary.Net.Dns;

namespace DnsServerCore.Dns.SmartResolver.Analysis
{
    public class ResponsePerformance
    {
        public string By { get; set; }
        public ResponseResult ResponseResult { get; set; }
        public long Max { get; set; }
        public double Average { get; set; }
        public long Min { get; set; }
       

        public override string ToString()
        {
            return $"By:{By,-24} Max:{Max,-5} Min:{Min,-5} Averaged:{Average:F1}";
        }
    }
}