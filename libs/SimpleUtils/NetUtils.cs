using System;
using System.Net;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;  // for GZIP (GZipStream class)
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;


namespace SimpleUtils
{
    public class NetUtils
    {

        public static uint IPv4AddressToInt(string ipv4AddrStr)
        {
            uint ipaddr = 0;

            try
            {
                string[] elements = ipv4AddrStr.Split('.');
                if (elements.Length == 4)
                {
                    ipaddr = Convert.ToUInt32(elements[0]) << 24;
                    ipaddr += Convert.ToUInt32(elements[1]) << 16;
                    ipaddr += Convert.ToUInt32(elements[2]) << 8;
                    ipaddr += Convert.ToUInt32(elements[3]);
                }

            }
            catch (Exception e)
            {
                Diagnostics.DumpException(e);
                ipaddr = 0;
            }

            return ipaddr;
        }

        public static string IPv4IntToString(uint ipaddr)
        {
            string ipv4AddrStr = null;

            try
            {
                uint val0 = ((ipaddr >> 0) & (0x0ff));
                uint val1 = ((ipaddr >> 8) & (0x0ff));
                uint val2 = ((ipaddr >> 16) & (0x0ff));
                uint val3 = ((ipaddr >> 24) & (0x0ff));

                ipv4AddrStr = String.Format("{0}.{1}.{2}.{3}", val3, val2, val1, val0);
            }
            catch (Exception e)
            {
                Diagnostics.DumpException(e);
                ipv4AddrStr = null;
            }

            return ipv4AddrStr;
        }


        /// <summary>
        /// Expand a CIDR (Classless Inter-Doman Routing) string like "10.0.1.0/24", defining a subnet address range,
        /// to a list of the component IP addresses.
        /// </summary>
        /// <param name="CIDRstr"></param>
        /// <returns>list of IP addresses</returns>
        public static List<string> ExpandCIDRToIPv4Addresses(string CIDRstr)
        {
            List<string> ipv4AddrList = new List<string>();

            try
            {
                string[] parts = CIDRstr.Trim().Split('/');
                if (parts.Length == 2)
                {
                    uint baseIPaddr = IPv4AddressToInt(parts[0]);
                    int numSignificantBits = Convert.ToInt32(parts[1]);

                    if ((baseIPaddr > 0) && (numSignificantBits > 0) && (numSignificantBits <= 32))
                    {
                        int numVariableBits = 32 - numSignificantBits;
                        int numAddresses = (1 << numVariableBits);

                        // make sure base address has zeroes for variable bits
                        baseIPaddr &= ~(((uint)1 << numVariableBits) - 1);

                        for (int i = 0; i < numAddresses; i++)
                        {
                            uint ipAddr = baseIPaddr + (uint)i;

                            string ipv4AddrStr = IPv4IntToString(ipAddr);
                            if (ipv4AddrStr != null)
                            {
                                ipv4AddrList.Add(ipv4AddrStr);
                            }
                            else
                            {
                                // this should never happen
                                break;
                            }
                        }
                    }
                }

            }
            catch (Exception e)
            {
                Console.WriteLine("SimpleUtils.NetUtils.ExpandCIDRToIPv4Addresses() : exception when expanding CIDR '{0}' ... ", CIDRstr);
                Diagnostics.DumpException(e);
                ipv4AddrList.Clear();
            }

            return ipv4AddrList;
        }


        public static Dictionary<string, string> TryReadFromAllIPv4URLs(List<string> ipv4AddrList, string queryUrl)
        {
            Dictionary<string, string> ipToResponseMap = new Dictionary<string, string>();

            try
            {
                if ((queryUrl == null) || (queryUrl.Length == 0))
                {
                    queryUrl = "/";
                }
                else if (!queryUrl.StartsWith("/"))
                {
                    queryUrl = "/" + queryUrl;
                }

                Dictionary<string, Thread> threadsMap = new Dictionary<string, Thread>();

                foreach (string ipaddrStr in ipv4AddrList)
                {
                    string fullQueryUrl = String.Format("http://{0}{1}", ipaddrStr, queryUrl);  // e.g. "http://10.0.0.0/ajax?cmd=blee?arg=blah"

                    Thread queryUrlThread = new Thread(() => TryReadFromURL_ThreadFunc(fullQueryUrl, ref ipToResponseMap, ipaddrStr));

                    threadsMap[ipaddrStr] = queryUrlThread;

                    queryUrlThread.Priority = ThreadPriority.BelowNormal;  // for responsivity, we usually we want the web server thread to have higher priority than background tasks
                    queryUrlThread.Start();
                }

                foreach (KeyValuePair<string, Thread> keyVal in threadsMap)
                {
                    string ipaddrStr = keyVal.Key;
                    Thread queryUrlThread = keyVal.Value;

                    queryUrlThread.Join();
                }

            }
            catch (Exception e)
            {
                Diagnostics.DumpException(e);
                ipToResponseMap.Clear();
            }

            return ipToResponseMap;
        }

        private static void TryReadFromURL_ThreadFunc(
            string fullQueryUrl,
            ref Dictionary<string, string> responsesMap,
            string responseKey)
        {
            try
            {
                // apparently a WebClient cannot be shared by threads
                WebClient webclient = new WebClient();

                string responseStr = webclient.DownloadString(fullQueryUrl);
                if ((responseStr != null) && (responseStr.Length > 0))
                {
                    //
                    // Each thread's responseKey should be unique;
                    // so we should be able to write to the shared responsesMap with no lock.
                    //
                    responsesMap[responseKey] = responseStr;
                }
            }
            catch (Exception e)
            {
                // fail silently
            }
        }

    }
}
