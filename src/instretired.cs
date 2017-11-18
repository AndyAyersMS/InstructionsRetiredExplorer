using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CoreClrInstRetired
{
    public class ImageInfo
    {
        public readonly string Name;
        public readonly ulong BaseAddress;
        public readonly int Size;
        public ulong SampleCount;
        public ulong EndAddress;
        public bool IsJitGeneratedCode;
        public bool IsJittedCode;
        public bool IsRejittedCode;
        public bool IsBackupImage;

        public ImageInfo(string name, ulong baseAddress, int size)
        {
            Name = name;
            BaseAddress = baseAddress;
            Size = size;
            EndAddress = baseAddress + (uint)Size;
            SampleCount = 0;
            IsJitGeneratedCode = false;
            IsBackupImage = false;
        }

        public static int LowerAddress(ImageInfo x, ImageInfo y)
        {
            if (x.BaseAddress < y.BaseAddress)
                return -1;
            else if (x.BaseAddress > y.BaseAddress)
                return 1;
            else if (x.EndAddress > y.EndAddress)
                return -1;
            else if (x.EndAddress > y.EndAddress)
                return 1;
            else
                return 0;
        }

        public static int MoreSamples(ImageInfo x, ImageInfo y)
        {
            if (x.SampleCount > y.SampleCount)
                return -1;
            else if (x.SampleCount < y.SampleCount)
                return 1;
            else
                return LowerAddress(x, y);
        }

        public bool ContainsAddress(ulong address)
        {
            return (address >= BaseAddress && address < EndAddress);
        }
    }

    public class JitInvocation
    {
        public int ThreadId;
        public long MethodId;
        public ulong InitialThreadCount;
        public ulong FinalThreadCount;
        public string MethodName;
        public JitInvocation PriorJitInvocation;
        public double InitialTimestamp;
        public double FinalTimestamp;

        public static int MoreJitInstructions(JitInvocation x, JitInvocation y)
        {
            ulong samplesX = x.JitInstrs();
            ulong samplesY = y.JitInstrs();
            if (samplesX < samplesY)
                return 1;
            else if (samplesY < samplesX)
                return -1;
            else return x.MethodId.CompareTo(y.MethodId);
        }

        public static int MoreJitTime(JitInvocation x, JitInvocation y)
        {
            double timeX = x.JitTime();
            double timeY = y.JitTime(); ;
            if (timeX < timeY)
                return 1;
            else if (timeY < timeX)
                return -1;
            else return x.MethodId.CompareTo(y.MethodId);
        }

        public double JitTime()
        {
            if (FinalTimestamp < InitialTimestamp) return 0;
            return (FinalTimestamp - InitialTimestamp);
        }

        public ulong JitInstrs()
        {
            if (FinalThreadCount < InitialThreadCount) return 0;
            return (FinalThreadCount - InitialThreadCount);
        }
    }

    public class Program
    {
        public static SortedDictionary<ulong, ulong> SampleCountMap = new SortedDictionary<ulong, ulong>();
        public static Dictionary<int, ulong> ThreadCountMap = new Dictionary<int, ulong>();
        public static Dictionary<string, ImageInfo> ImageMap = new Dictionary<string, ImageInfo>();
        public static Dictionary<int, JitInvocation> ActiveJitInvocations = new Dictionary<int, JitInvocation>();
        public static List<JitInvocation> AllJitInvocations = new List<JitInvocation>();
        public static ulong JitSampleCount = 0;
        public static ulong TotalSampleCount = 0;
        public static ulong JitGeneratedCodeSampleCount = 0;
        public static ulong JittedCodeSampleCount = 0;
        public static ulong UnknownImageCount = 0;
        public static ulong JittedCodeSize = 0;
        public static ulong ManagedMethodCount = 0;
        public static ulong PMCInterval = 65536;
        public static string jitDllKey;
        public static double ProcessStart;
        public static double ProcessEnd;

        static void UpdateSampleCountMap(ulong address, ulong count)
        {
            if (!SampleCountMap.ContainsKey(address))
            {
                SampleCountMap[address] = 0;
            }
            SampleCountMap[address] += count;
            TotalSampleCount += count;
        }

        static void UpdateThreadCountMap(int threadId, ulong count)
        {
            if (!ThreadCountMap.ContainsKey(threadId))
            {
                ThreadCountMap[threadId] = 0;
            }
            ThreadCountMap[threadId] += count;
        }

        static void AttributeSampleCounts()
        {
            // Sort images by starting address.
            ImageInfo[] imageArray = new ImageInfo[ImageMap.Count];
            ImageMap.Values.CopyTo(imageArray, 0);
            Array.Sort(imageArray, ImageInfo.LowerAddress);
            int index = 0;
            int backupIndex = 0;

            foreach (ulong address in SampleCountMap.Keys)
            {
                ImageInfo image = null;

                // See if any non-backup image can claim this address.
                for (int i = index; i < imageArray.Length; i++)
                {
                    if (!imageArray[i].IsBackupImage && imageArray[i].ContainsAddress(address))
                    {
                        image = imageArray[i];
                        index = i;
                        break;
                    }
                }

                // If that fails, see if any backup image can claim this address
                if (image == null)
                {
                    for (int i = backupIndex; i < imageArray.Length; i++)
                    {
                        if (imageArray[i].IsBackupImage && imageArray[i].ContainsAddress(address))
                        {
                            image = imageArray[i];
                            backupIndex = i;
                            break;
                        }
                    }
                }

                ulong counts = SampleCountMap[address];

                if (image == null)
                {
                    bool significant = ((double)counts / TotalSampleCount) > 0.001;
                    if (significant)
                    {
                        Console.WriteLine("Can't map address {0:X} -- {1} counts", address, SampleCountMap[address]);
                    }
                    UnknownImageCount += counts;
                    continue;
                }

                image.SampleCount += counts;

                if (image.IsJitGeneratedCode)
                {
                    JitGeneratedCodeSampleCount += counts;
                }
                if (image.IsJittedCode)
                {
                    JittedCodeSampleCount += counts;
                }
                continue;
            }
        }

        private static string GetName(MethodLoadUnloadVerboseTraceData data, string assembly)
        {
            // Prepare sig (strip return value)
            var sig = "";
            var sigWithRet = data.MethodSignature;
            var parenIdx = sigWithRet.IndexOf('(');
            if (0 <= parenIdx)
                sig = sigWithRet.Substring(parenIdx);

            // prepare class name (strip namespace)
            var className = data.MethodNamespace;
            var lastDot = className.LastIndexOf('.');
            var firstBox = className.IndexOf('[');
            if (0 <= lastDot && (firstBox < 0 || lastDot < firstBox))
                className = className.Substring(lastDot + 1);
            var sep = ".";
            if (className.Length == 0)
                sep = "";

            return assembly + className + sep + data.MethodName + sig;
        }

        public static int Main(string[] args)
        {
            if (args.Length < 1 || args.Length > 4)
            {
                Console.WriteLine("Usage: instretired file.etl [-process process-name] [-show-events]");
                Console.WriteLine("   process defaults to corerun");
                return -1;
            }

            string traceFile = args[0];

            if (!File.Exists(traceFile))
            {
                Console.WriteLine($"Can't find trace file '{traceFile}'");
                return -1;
            }

            string benchmarkName = "corerun";
            bool showEvents = false;
            int benchmarkPid = -2;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-process":
                        {
                            if (i + 1 == args.Length)
                            {
                                Console.WriteLine($"Missing process name after '{args[i]}'");
                            }
                            benchmarkName = args[i + 1];
                            i++;
                        }
                        break;
                    case "-show-events":
                        showEvents = true;
                        break;
                    default:
                        Console.WriteLine($"Unknown arg '{args[i]}'");
                        return -1;
                }
            }

            Console.WriteLine("Mining ETL from {0} for process {1}", traceFile, benchmarkName);

            Dictionary<string, uint> allEventCounts = new Dictionary<string, uint>();
            Dictionary<string, uint> eventCounts = new Dictionary<string, uint>();
            Dictionary<string, uint> processCounts = new Dictionary<string, uint>();
            Dictionary<long, ModuleLoadUnloadTraceData> moduleInfo = new Dictionary<long, ModuleLoadUnloadTraceData>();
            Dictionary<long, string> assemblyNames = new Dictionary<long, string>();

            using (var source = new ETWTraceEventSource(traceFile))
            {
                source.Kernel.All += delegate (TraceEvent data)
                {
                    if (allEventCounts.ContainsKey(data.EventName))
                    {
                        allEventCounts[data.EventName]++;
                    }
                    else
                    {
                        allEventCounts[data.EventName] = 1;
                    }

                    if (data.ProcessID == benchmarkPid)
                    {
                        if (eventCounts.ContainsKey(data.EventName))
                        {
                            eventCounts[data.EventName]++;
                        }
                        else
                        {
                            eventCounts[data.EventName] = 1;
                        }
                    }

                    switch (data.EventName)
                    {
                        case "Process/Start":
                        case "Process/DCStart":
                            {
                                // Process was running when tracing started (DCStart)
                                // or started when tracing was running (Start)
                                ProcessTraceData pdata = (ProcessTraceData)data;
                                if (String.Equals(pdata.ProcessName, benchmarkName, StringComparison.OrdinalIgnoreCase))
                                {
                                    Console.WriteLine("Found process [{0}] {1}: {2}", pdata.ProcessID, pdata.ProcessName, pdata.CommandLine);
                                    benchmarkPid = pdata.ProcessID;
                                    ProcessStart = pdata.TimeStampRelativeMSec;
                                }
                                else
                                {
                                    // Console.WriteLine("Ignoring events from process {0}", pdata.ProcessName);
                                }
                                break;
                            }

                        case "Image/DCStart":
                            {
                                ImageLoadTraceData imageLoadTraceData = (ImageLoadTraceData)data;

                                if (data.ProcessID == 0 || data.ProcessID == benchmarkPid)
                                {
                                    string fileName = imageLoadTraceData.FileName;
                                    ulong imageBase = imageLoadTraceData.ImageBase;
                                    int imageSize = imageLoadTraceData.ImageSize;

                                    string fullName = fileName + "@" + imageBase.ToString();

                                    if (!ImageMap.ContainsKey(fullName))
                                    {
                                        ImageInfo imageInfo = new ImageInfo(Path.GetFileName(fileName), imageBase, imageSize);
                                        ImageMap.Add(fullName, imageInfo);
                                    }
                                }

                                break;
                            }

                        case "Image/Load":
                            {
                                ImageLoadTraceData imageLoadTraceData = (ImageLoadTraceData)data;

                                if (imageLoadTraceData.ProcessID == benchmarkPid)
                                {
                                    string fileName = imageLoadTraceData.FileName;
                                    ulong imageBase = imageLoadTraceData.ImageBase;
                                    int imageSize = imageLoadTraceData.ImageSize;

                                    // Hackily suppress ngen images here, otherwise we lose visibility
                                    // into ngen methods...

                                    string fullName = fileName + "@" + imageBase.ToString();

                                    if (!ImageMap.ContainsKey(fullName))
                                    {
                                        ImageInfo imageInfo = new ImageInfo(Path.GetFileName(fileName), imageBase, imageSize);

                                        if (fileName.Contains("Microsoft.") || fileName.Contains("System.") || fileName.Contains("Newtonsoft."))
                                        {
                                            imageInfo.IsBackupImage = true;
                                        }

                                        ImageMap.Add(fullName, imageInfo);

                                        if (fileName.Contains("clrjit.dll"))
                                        {
                                            jitDllKey = fullName;
                                        }


                                    }
                                }

                                break;
                            }

                        case "PerfInfo/PMCSample":
                            {
                                PMCCounterProfTraceData traceData = (PMCCounterProfTraceData)data;
                                if (traceData.ProcessID == benchmarkPid)
                                {
                                    ulong instructionPointer = traceData.InstructionPointer;
                                    // Not sure how to find the PMC reload interval... sigh
                                    ulong count = 1;
                                    UpdateSampleCountMap(instructionPointer, count);
                                    UpdateThreadCountMap(traceData.ThreadID, count);
                                }
                                break;
                            }

                        case "PerfInfo/CollectionStart":
                            SampledProfileIntervalTraceData sampleData = (SampledProfileIntervalTraceData)data;
                            PMCInterval = (ulong)sampleData.NewInterval;
                            Console.WriteLine($"PMC interval now {PMCInterval}");
                            break;
                    }
                };

                source.Clr.All += delegate (TraceEvent data)
                {
                    if (allEventCounts.ContainsKey(data.EventName))
                    {
                        allEventCounts[data.EventName]++;
                    }
                    else
                    {
                        allEventCounts[data.EventName] = 1;
                    }

                    if (data.ProcessID == benchmarkPid)
                    {
                        if (eventCounts.ContainsKey(data.EventName))
                        {
                            eventCounts[data.EventName]++;
                        }
                        else
                        {
                            eventCounts[data.EventName] = 1;
                        }

                        switch (data.EventName)
                        {
                            case "Loader/AssemblyLoad":
                                {
                                    AssemblyLoadUnloadTraceData assemblyData = (AssemblyLoadUnloadTraceData)data;
                                    string assemblyName = assemblyData.FullyQualifiedAssemblyName;
                                    int cpos = assemblyName.IndexOf(',');
                                    string shortAssemblyName = '[' + assemblyName.Substring(0, cpos) + ']';
                                    assemblyNames[assemblyData.AssemblyID] = shortAssemblyName;
                                    // Console.WriteLine($"Assembly {shortAssemblyName} at 0x{assemblyData.AssemblyID:X} ");
                                    break;
                                }
                            case "Loader/ModuleLoad":
                                {
                                    ModuleLoadUnloadTraceData moduleData = (ModuleLoadUnloadTraceData)data;
                                    moduleInfo[moduleData.ModuleID] = moduleData;
                                    break;
                                }
                            case "Method/JittingStarted":
                                {
                                    MethodJittingStartedTraceData jitStartData = (MethodJittingStartedTraceData)data;
                                    JitInvocation jitInvocation = new JitInvocation();
                                    jitInvocation.ThreadId = jitStartData.ThreadID;
                                    jitInvocation.MethodId = jitStartData.MethodID;
                                    jitInvocation.InitialTimestamp = jitStartData.TimeStampRelativeMSec;
                                    UpdateThreadCountMap(jitInvocation.ThreadId, 0); // hack
                                    jitInvocation.InitialThreadCount = ThreadCountMap[jitInvocation.ThreadId];
                                    if (ActiveJitInvocations.ContainsKey(jitInvocation.ThreadId))
                                    {
                                        jitInvocation.PriorJitInvocation = ActiveJitInvocations[jitInvocation.ThreadId];
                                        ActiveJitInvocations.Remove(jitInvocation.ThreadId);
                                    }
                                    ActiveJitInvocations.Add(jitInvocation.ThreadId, jitInvocation);
                                    AllJitInvocations.Add(jitInvocation);
                                    break;
                                }
                            case "Method/LoadVerbose":
                                {
                                    MethodLoadUnloadVerboseTraceData loadUnloadData = (MethodLoadUnloadVerboseTraceData)data;

                                    JitInvocation j = null;

                                    if (ActiveJitInvocations.ContainsKey(loadUnloadData.ThreadID))
                                    {
                                        j = ActiveJitInvocations[loadUnloadData.ThreadID];
                                        ActiveJitInvocations.Remove(j.ThreadId);
                                        if (j.PriorJitInvocation != null)
                                        {
                                            ActiveJitInvocations.Add(j.ThreadId, j.PriorJitInvocation);
                                        }
                                        j.FinalThreadCount = ThreadCountMap[j.ThreadId];
                                        j.FinalTimestamp = loadUnloadData.TimeStampRelativeMSec;
                                        JitSampleCount += j.JitInstrs();
                                        ManagedMethodCount++;
                                        JittedCodeSize += (ulong)loadUnloadData.MethodExtent;
                                    }
                                    else
                                    {
                                        // ?
                                    }

                                    // Pretend this is an "image"
                                    long assemblyId = moduleInfo[loadUnloadData.ModuleID].AssemblyID;
                                    string assemblyName = "";
                                    if (assemblyNames.ContainsKey(assemblyId))
                                    {
                                        assemblyName = assemblyNames[assemblyId];
                                    }

                                    string fullName = GetName(loadUnloadData, assemblyName);
                                    if (j != null) j.MethodName = fullName;
                                    // string key = fullName + "@" + loadUnloadData.MethodID.ToString("X");
                                    string key = loadUnloadData.MethodID.ToString("X");
                                    if (!ImageMap.ContainsKey(key))
                                    {
                                        ImageInfo methodInfo = new ImageInfo(fullName, loadUnloadData.MethodStartAddress,
                                           loadUnloadData.MethodSize);
                                        ImageMap.Add(key, methodInfo);
                                        methodInfo.IsJitGeneratedCode = true;
                                        methodInfo.IsJittedCode = loadUnloadData.IsJitted;
                                        methodInfo.IsRejittedCode = false; // needs V2 parser
                                    }

                                    break;
                                }
                            case "Method/UnloadVerbose":
                                {
                                    // Pretend this is an "image"
                                    MethodLoadUnloadVerboseTraceData loadUnloadData = (MethodLoadUnloadVerboseTraceData)data;

                                    long assemblyId = moduleInfo[loadUnloadData.ModuleID].AssemblyID;
                                    string assemblyName = "";
                                    if (assemblyNames.ContainsKey(assemblyId))
                                    {
                                        assemblyName = assemblyNames[assemblyId];
                                    }
                                    string fullName = GetName(loadUnloadData, assemblyName);
                                    // string key = fullName + "@" + loadUnloadData.MethodID.ToString("X");
                                    string key = loadUnloadData.MethodID.ToString("X");
                                    if (!ImageMap.ContainsKey(key))
                                    {
                                        // Pretend this is an "image"
                                        ImageInfo methodInfo = new ImageInfo(fullName, loadUnloadData.MethodStartAddress,
                                            loadUnloadData.MethodSize);
                                        ImageMap.Add(key, methodInfo);
                                        methodInfo.IsJitGeneratedCode = true;
                                        methodInfo.IsJittedCode = loadUnloadData.IsJitted;
                                    }
                                    else
                                    {
                                        // Console.WriteLine("eh? see method {0} again in rundown", fullName);
                                    }
                                }
                                break;
                        }
                    }
                };

                source.Process();
            };

            AttributeSampleCounts();

            if (showEvents)
            {
                Console.WriteLine("Event Breakdown");

                foreach (var e in allEventCounts)
                {
                    Console.WriteLine("Event {0} occurred {1} times", e.Key, e.Value);
                }
            }

            if (!eventCounts.ContainsKey("PerfInfo/PMCSample"))
            {
                Console.WriteLine("No PMC events seen for {0}, sorry.", benchmarkName);
            }
            else
            {
                ulong InstrsPerEvent = PMCInterval;
                ulong pmcEvents = eventCounts["PerfInfo/PMCSample"];
                ulong JitDllSampleCount = ImageMap[jitDllKey].SampleCount;
                ulong JitInterfaceCount = JitSampleCount - JitDllSampleCount;

                Console.WriteLine("InstRetired for {0}: {1} events, {2:E} instrs",
                    benchmarkName, pmcEvents, pmcEvents * InstrsPerEvent);

                if (AllJitInvocations.Count > 0)
                {
                    Console.WriteLine("Jitting           : {0:00.00%} {1,-8:G3} instructions {2} methods",
                        (double)JitSampleCount / TotalSampleCount, JitSampleCount * InstrsPerEvent, AllJitInvocations.Count);
                    Console.WriteLine("  JitInterface    : {0:00.00%} {1,-8:G3} instructions",
                        (double)JitInterfaceCount / TotalSampleCount, JitInterfaceCount * InstrsPerEvent);
                    Console.WriteLine("Jit-generated code: {0:00.00%} {1,-8:G3} instructions",
                        (double)JitGeneratedCodeSampleCount / TotalSampleCount, JitGeneratedCodeSampleCount * InstrsPerEvent);
                    Console.WriteLine("  Jitted code     : {0:00.00%} {1,-8:G3} instructions",
                        (double)JittedCodeSampleCount / TotalSampleCount, JittedCodeSampleCount * InstrsPerEvent);
                    Console.WriteLine();
                }

                double ufrac = (double)UnknownImageCount / TotalSampleCount;
                if (ufrac > 0.002)
                {
                    Console.WriteLine("{0:00.00%}   {1,-8:G3}    {2} {3}",
                        ufrac,
                        UnknownImageCount * InstrsPerEvent,
                        "?      ",
                        "Unknown");
                }

                // Collect up significant counts
                List<ImageInfo> significantInfos = new List<ImageInfo>();

                foreach (var i in ImageMap)
                {
                    double frac = (double)i.Value.SampleCount / TotalSampleCount;
                    if (frac > 0.0005)
                    {
                        significantInfos.Add(i.Value);
                    }
                }

                significantInfos.Sort(ImageInfo.MoreSamples);

                foreach (var i in significantInfos)
                {
                    Console.WriteLine("{0:00.00%}   {1,-9:G4}   {2}  {3}",
                        (double)i.SampleCount / TotalSampleCount,
                        i.SampleCount * InstrsPerEvent,
                        i.IsJitGeneratedCode ? (i.IsJittedCode ? (i.IsRejittedCode ? "rejit " : "jit   ") : "prejit") : "native",
                        i.Name);
                }

                // Show significant jit invocations (samples)
                AllJitInvocations.Sort(JitInvocation.MoreJitInstructions);
                bool printed = false;
                ulong signficantCount = (5 * JitSampleCount) / 1000;
                foreach (var j in AllJitInvocations)
                {
                    ulong totalCount = j.JitInstrs();
                    if (totalCount > signficantCount)
                    {
                        if (!printed)
                        {
                            Console.WriteLine();
                            Console.WriteLine("Slow jitting methods (anything taking more than 0.5% of total samples)");
                            printed = true;
                        }
                        Console.WriteLine("{0:00.00%}    {1,-9:G4} {2}", (double)totalCount / TotalSampleCount, totalCount * InstrsPerEvent, j.MethodName);
                    }
                }

                Console.WriteLine();
                double totalJitTime = AllJitInvocations.Sum(j => j.JitTime());
                Console.WriteLine($"Total jit time: {totalJitTime:F2}ms {AllJitInvocations.Count} methods {totalJitTime/ AllJitInvocations.Count:F2}ms avg");

                // Show 10 slowest jit invocations (time, ms)
                AllJitInvocations.Sort(JitInvocation.MoreJitTime);
                Console.WriteLine();
                Console.WriteLine($"Slow jitting methods (time)");
                int kLimit = 10;
                for (int k = 0; k < kLimit; k++)
                {
                    if (k < AllJitInvocations.Count)
                    {
                        JitInvocation j = AllJitInvocations[k];
                        Console.WriteLine($"{j.JitTime(),6:F2} {j.MethodName} starting at {j.InitialTimestamp,6:F2}");
                    }
                }

                // Show data on cumulative distribution of jit times.
                Console.WriteLine();
                Console.WriteLine("Jit time percentiles");
                for (int percentile = 10; percentile <= 100; percentile += 10)
                {
                    int pIndex = (AllJitInvocations.Count * (100 - percentile)) / 100;
                    JitInvocation p = AllJitInvocations[pIndex];
                    Console.WriteLine($"{percentile,3:D}%ile jit time is {p.JitTime():F3}ms");
                }
            }

            return 0;

        }
    }
}
