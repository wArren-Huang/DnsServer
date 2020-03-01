using System.Threading.Tasks;
using TechnitiumLibrary.Net.Dns;

namespace DnsServerCore.Dns.SmartResolver
{
    public class SmartResolver
    {
        private const int TransientAnswerTtl = 5;
        
        public static DnsDatagram Resolve(DnsQuestionRecord questionRecord, DnsResolverConfig dnsResolverConfig, DnsCache dnsCache)
        {
            var question = questionRecord.Name;
            if (ResolvingPreference.IsKnown(question))
            {
                var response = ReliableResolver.Resolve(questionRecord, dnsResolverConfig);
                if (response != null && response.Answer.Length > 0)
                {
                    dnsCache.CacheResponse(response);
                }
                return response;
            }
            else
            {
                var response = FastResolver.Resolve(questionRecord, dnsResolverConfig);
                Task.Factory.StartNew(() =>
                {
                    var reliableResponse = ReliableResolver.Resolve(questionRecord, dnsResolverConfig);
                    dnsCache.CacheResponse(reliableResponse);
                });
                for (var i=0; i<response.Answer.Length; i++)
                {
                    var answer = response.Answer[i];
                    var transientAnswer = 
                        new DnsResourceRecord(answer.Name, answer.Type, answer.Class, TransientAnswerTtl, answer.RDATA);
                    response.Answer[i] = transientAnswer;
                }

                return response;
            }
        }
    }
}