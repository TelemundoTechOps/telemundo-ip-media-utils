using System;
using Mono.Options;
using Newtonsoft.Json;

namespace IGMPSpeedTest
{
    class Program
    {
        /// <summary>
        /// IgmpSpeedTest - measures IGMP JOIN and LEAVE performance
        /// 
        /// Copyright(C) 2016 NBCUniversal
        /// 
        /// This library is free software; you can redistribute it and/or
        /// modify it under the terms of the GNU Lesser General Public
        /// License as published by the Free Software Foundation; either
        /// version 2.1 of the License, or(at your option) any later version.
        /// 
        /// This library is distributed in the hope that it will be useful,
        /// but WITHOUT ANY WARRANTY; without even the implied warranty of
        /// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the GNU
        /// Lesser General Public License for more details.
        /// 
        /// You should have received a copy of the GNU Lesser General Public
        /// License along with this library; if not, write to the Free Software
        /// Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
        /// 
        /// This application operates as a signal generator and a signal analyzer.
        /// One instance emites 1-254 multicast streams, while another instance
        /// issues IGMP JOINs to those streams and measures how long it takes for the network
        /// to begin data delivery on each stream.
        /// 
        /// The receiving application can also measure LEAVE performance.  Requires WinPcap.
        /// Notes:  
        ///  - LEAVE performance measurements not reliably accurate, probably due to
        ///  trigger buffer issues in the WINPCAP or SharpPcap libraries
        ///  - The emitter and receiver must run on different servers to avoid multicast
        /// loopback problems
        ///  - Measurement accuracy varies across servers and networks, but it reported by
        /// the application.  High resolution timers are used, so sub-milliscond accuracy is
        /// expected in most environments.
        /// 
        /// Developed by intoto systems as a work-for-hire for NBCUniversal, and released
        /// under the LGPL per NBCUniversal request.
        /// </summary> 
        /// <param name="args"></param>
        static void Main(string[] args)
        {
            var localIp = "127.0.0.1";
            var firstGroup ="230.8.97.1";
            short ttl = 32;
            var streamCount = 1;
            var receiverFlag = false;
            var includeLeave = false;
            var timeOutSeconds = 5;
            var quietMode = false;
            var json = false;
            var helpWanted = false;
            var pauseBeforeLeave = false;
            var startAt = "";
            DateTime? startTime = null;

            // Parse command-line options
            var cmdLineOptions = new OptionSet()
            {
                { "l|local=","IP address of local NIC to use (default: 127.0.0.1)", v => localIp =v},
                { "n|network=","Starting multicast group address (default: 230.8.97.1)", v => firstGroup =v},
                { "c|count=","Number of multicast streams to test (default: 1)", (int v) => streamCount=v},
                { "leave","Include IGMP LEAVE tests (requires WinPcap 4.1.3)", v => includeLeave= v != null},
                { "r|receiver","This instance will JOIN the stream instead of emit the stream.", v => receiverFlag=v != null},                
                { "t|timeout=","Duration (in seconds) to wait for data to arrive on multicast before quitting (default: 5)",(int v) => timeOutSeconds=v},
                { "ttl=","Time to live for emitted packets (default: 32)",(short v) => ttl=v},
                { "q|quiet","Just do it, dont ask.",v => quietMode = v !=null},
                { "json","Output results in JSON",v => json = v !=null},
                { "pbl","Pause before issuing LEAVE messages",v=> pauseBeforeLeave = v !=null },
                { "startat=","Time to start", (string v) => startAt = v},
                { "h|help","Show this message",v => helpWanted = v !=null}
            };

            try
            {
                cmdLineOptions.Parse(args);
            }
            catch (OptionException e)
            {
                Console.Write("IGMPSpeedTest:");
                Console.WriteLine(e.Message);
                Console.WriteLine("Try IGMPSpeedTest --help for more information");
                return;
            }

            if (helpWanted)
            {
                ShowHelp(cmdLineOptions);
                return;
            }

            if (!String.IsNullOrEmpty(startAt))
            {
                DateTime ParsedDate = new DateTime();
                var result = DateTime.TryParse(startAt, out ParsedDate);
                if(result != true)
                {
                    Console.WriteLine("Cannot parse start time: {0}", startAt);
                    return;
                }
                startTime = ParsedDate;                                
            }

            if (pauseBeforeLeave && includeLeave)
            {
                Console.WriteLine("Cannot select both --leave and --pbl");
                return;
            }

            if (pauseBeforeLeave && !receiverFlag)
            {
                Console.WriteLine("Pause Before Leave (--pbl) only applies when running in receiver (-r) mode");
                return;
            }

            IgmpSpeedTest speedTester;



            try
            {
                speedTester = new IgmpSpeedTest(localIp,firstGroup ,streamCount,timeOutSeconds,quietMode,receiverFlag,ttl,includeLeave,startTime);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);                
                return;
            }

            var results = speedTester.BeginTest();

            if (!receiverFlag) return;

            // display results
            if (!quietMode && !json)
            {
                Console.WriteLine("JOIN times:");
                foreach (var r in results.JoinTime)
                    Console.WriteLine(r.Value == -1.0 ? "  {0}: --" : "  {0}: {1}", r.Key, r.Value);
                if (includeLeave && !pauseBeforeLeave)
                {
                    Console.WriteLine("\nLEAVE times:");
                    foreach (var r in results.LeaveTime)
                        Console.WriteLine(r.Value == -1.0 ? "  {0}: --" : "  {0}: {1}", r.Key, r.Value);
                }
                Console.WriteLine("");
            }

            if (!json)
            { 
                if (results.HasResults)  // did we get packets for any groups?
                {
                    Console.WriteLine("Best JOIN performance: {0}", results.FastestJoin);
                    Console.WriteLine("Average JOIN performance: {0}", results.AverageJoin);
                    Console.WriteLine("Worst JOIN performance: {0}\n", results.SlowestJoin);
                    if (includeLeave && ! pauseBeforeLeave)
                    {
                        Console.WriteLine("Best LEAVE performance: {0}", results.FastestLeave);
                        Console.WriteLine("Average LEAVE performance: {0}", results.AverageLeave);
                        Console.WriteLine("Worst LEAVE performance: {0}", results.SlowestLeave);
                    }
                    Console.WriteLine("\nAll times expressed in usecs");}
                else
                {
                    Console.WriteLine("No packets received on any expected group.");
                }
            }
            else
                Console.WriteLine(JsonConvert.SerializeObject(results,Formatting.Indented));
            
            if (pauseBeforeLeave)
            {
                Console.WriteLine("Press ENTER to issue LEAVE messages then quit...");
                Console.ReadLine();

            }
        }

        /// <summary>
        /// Shows the supported command-line options
        /// </summary>
        /// <param name="cmdLineOptions"></param>
        static void ShowHelp(OptionSet cmdLineOptions)
        {
            Console.WriteLine("IGMPSpeedTest - by intoto systems.");
            Console.WriteLine("Version: " + System.Windows.Forms.Application.ProductVersion);
            Console.WriteLine("A very basic IGMP JOIN speed measurement tool.");
            Console.WriteLine("Usage:  Program [OPTIONS]");
            cmdLineOptions.WriteOptionDescriptions(Console.Out);
        }

    
    }
}

