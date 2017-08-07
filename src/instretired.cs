using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using System;
using System.Collections.Generic;
using System.IO;

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

        public ImageInfo(string name, ulong baseAddress, int size)
        {
            Name = name;
            BaseAddress = baseAddress;
            Size = size;
            EndAddress = baseAddress + (uint)Size;
            SampleCount = 0;
            IsJitGeneratedCode = false;
        }

        public static int LowerAddress(ImageInfo x, ImageInfo y)
        {
            if (x.BaseAddress < y.BaseAddress)
                return -1;
            else if (x.BaseAddress > y.BaseAddress)
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
            return (BaseAddress >= address && EndAddress < address);
        }
    }

    public class JitInvocation
    {
        public int ThreadId;
        public long MethodId;
        public ulong InitialThreadCount;
        public ulong FinalThreadCount;
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

            // Walk sorted sample count map and attribute counts.
            int imageIndex = 0;
            ImageInfo image = imageArray[imageIndex];

            foreach (ulong address in SampleCountMap.Keys)
            {
                while ((imageIndex < imageArray.Length) && (address >= image.EndAddress))
                {
                    ImageInfo nextImage = imageArray[imageIndex++];
                    if (nextImage.BaseAddress < image.EndAddress)
                    {
                        if ((nextImage.BaseAddress != image.BaseAddress)
                            || (nextImage.EndAddress != image.EndAddress)
                            || !nextImage.Name.Equals(image.Name))
                        {
                            Console.WriteLine("eh? {0} [{1:X}-{2:X}) and {3} [{4:X}-{5:X}) overlap",
                                image.Name, image.BaseAddress, image.EndAddress,
                                nextImage.Name, nextImage.BaseAddress, nextImage.EndAddress);
                        }
                    }

                    image = nextImage;
                }

                if (address >= image.EndAddress || address < image.BaseAddress)
                {
                    if (UnknownImageCount < 4)
                    {
                        Console.WriteLine("Can't map address {0:X}", address);
                    }
                    UnknownImageCount += SampleCountMap[address];
                    continue;
                }

                image.SampleCount += SampleCountMap[address];
                if (image.IsJitGeneratedCode)
                {
                    JitGeneratedCodeSampleCount += SampleCountMap[address];
                }
                if (image.IsJittedCode)
                {
                    JittedCodeSampleCount += SampleCountMap[address];
                }
                continue;
            }
        }

        private static string GetName(MethodLoadUnloadVerboseTraceData data)
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

            return className + sep + data.MethodName + sig;
        }

        public static int Main(string[] args)
        {
            if (args.Length != 1 && args.Length != 3)
            {
                Console.WriteLine("Usage: instretired file.etl [-process process-name]");
                Console.WriteLine("   process defaults to corerun");
                return -1;
            }

            string traceFile = args[0];
            string benchmarkName = "corerun";
            int benchmarkPid = -2;
            if (args.Length == 3)
            {
                benchmarkName = args[2];
            }

            Console.WriteLine("Mining ETL from {0} for process {1}", traceFile, benchmarkName);

            Dictionary<string, uint> allEventCounts = new Dictionary<string, uint>();
            Dictionary<string, uint> eventCounts = new Dictionary<string, uint>();
            Dictionary<string, uint> processCounts = new Dictionary<string, uint>();

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

                                if (data.ProcessID == 0)
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

                                    // Suppress ngen images here, otherwise we lose visibility
                                    // into ngen methods...

                                    if (fileName.Contains(".ni."))
                                    {
                                        break;
                                    }

                                    string fullName = fileName + "@" + imageBase.ToString();

                                    if (!ImageMap.ContainsKey(fullName))
                                    {
                                        ImageInfo imageInfo = new ImageInfo(Path.GetFileName(fileName), imageBase, imageSize);
                                        ImageMap.Add(fullName, imageInfo);
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
                            case "Method/JittingStarted":
                                {
                                    MethodJittingStartedTraceData jitStartData = (MethodJittingStartedTraceData)data;
                                    JitInvocation jitInvocation = new JitInvocation();
                                    jitInvocation.ThreadId = jitStartData.ThreadID;
                                    jitInvocation.MethodId = jitStartData.MethodID;
                                    jitInvocation.InitialThreadCount = ThreadCountMap[jitInvocation.ThreadId];
                                    ActiveJitInvocations.Add(jitInvocation.ThreadId, jitInvocation);
                                    AllJitInvocations.Add(jitInvocation);
                                    break;
                                }
                            case "Method/LoadVerbose":
                                {
                                    MethodLoadUnloadVerboseTraceData loadUnloadData = (MethodLoadUnloadVerboseTraceData)data;
                                    if (ActiveJitInvocations.ContainsKey(loadUnloadData.ThreadID))
                                    {
                                        JitInvocation j = ActiveJitInvocations[loadUnloadData.ThreadID];
                                        ActiveJitInvocations.Remove(j.ThreadId);
                                        j.FinalThreadCount = ThreadCountMap[j.ThreadId];
                                        if (j.FinalThreadCount < j.InitialThreadCount)
                                        {
                                            Console.WriteLine("eh? negative jit count...");
                                        }
                                        else
                                        {
                                            JitSampleCount += j.FinalThreadCount - j.InitialThreadCount;
                                        }

                                        ManagedMethodCount++;
                                        JittedCodeSize += (ulong)loadUnloadData.MethodExtent;
                                    }
                                    else
                                    {
                                        // ?
                                    }
                                    break;
                                }
                            }
                        case "Method/UnloadVerbose":
                            {
                                // Pretend this is an "image"
                                MethodLoadUnloadVerboseTraceData loadUnloadData = (MethodLoadUnloadVerboseTraceData)data;
                                string fullName = GetName(loadUnloadData);
                                string key = fullName + "@" + loadUnloadData.MethodID.ToString("X");
                                if (!ImageMap.ContainsKey(key))
                                {
                                    // Pretend this is an "image"
                                    MethodLoadUnloadVerboseTraceData loadUnloadData = (MethodLoadUnloadVerboseTraceData)data;
                                    string fullName = GetName(loadUnloadData);
                                    string key = fullName + "@" + loadUnloadData.MethodID.ToString("X");
                                    if (!ImageMap.ContainsKey(key))
                                    {
                                        ImageInfo methodInfo = new ImageInfo(fullName, loadUnloadData.MethodStartAddress,
                                            loadUnloadData.MethodSize);
                                        ImageMap.Add(key, methodInfo);
                                        methodInfo.IsJitGeneratedCode = true;
                                        methodInfo.IsJittedCode = loadUnloadData.IsJitted;
                                    }
                                    //else
                                    //{
                                    //    Console.WriteLine("eh? see method {0} again in rundown", fullName);
                                    //}
                                    break;
                                }
                        }
                    }
                };

                source.Process();
            }


            AttributeSampleCounts();

            foreach (var e in allEventCounts)
            {
                Console.WriteLine("Event {0} occurred {1} times", e.Key, e.Value);
            }

            if (!eventCounts.ContainsKey("PerfInfo/PMCSample"))
            {
                Console.WriteLine("No PMC events seen, sorry.");
            }
            else
            {
                ulong InstrsPerEvent = 65536;
                ulong pmcEvents = eventCounts["PerfInfo/PMCSample"];

                Console.WriteLine("InstRetired for {0}: {1} events, {2:E} instrs",
                    benchmarkName, pmcEvents, pmcEvents * InstrsPerEvent);
                Console.WriteLine("Jitting           : {0:00.00%} ({1} methods)",
                    (double)JitSampleCount / TotalSampleCount, AllJitInvocations.Count);
                // Console.WriteLine("  JitInterface    : {0:00.00%}", (double) JitSampleCount - JitDllSampleCount);
                Console.WriteLine("Jit-generated code: {0:00.00%}", (double)JitGeneratedCodeSampleCount / TotalSampleCount);
                Console.WriteLine("  Jitted code     : {0:00.00%}", (double)JittedCodeSampleCount / TotalSampleCount);
                Console.WriteLine();

                double ufrac = (double)UnknownImageCount / TotalSampleCount;
                if (ufrac > 0.002)
                {
                    Console.WriteLine("{0:00.00%}   {1,-8:G3}   {2} {3}",
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
                    if (frac > 0.002)
                    {
                        significantInfos.Add(i.Value);
                    }
                }

                significantInfos.Sort(ImageInfo.MoreSamples);

                foreach (var i in significantInfos)
                {
                    Console.WriteLine("{0:00.00%}   {1,-8:G3}   {2}  {3}",
                        (double)i.SampleCount / TotalSampleCount,
                        i.SampleCount * InstrsPerEvent,
                        i.IsJitGeneratedCode ? (i.IsJittedCode ? "jit   " : "prejit") : "native",
                        i.Name);
                }
            }

            return 0;
        }
    }
}
