setlocal

set CORE_ROOT=C:\repos\coreclr\bin\tests\Windows_NT.x64.release\Tests\Core_Root
cd /d %CORE_ROOT%

goto :CLR 

c:\xperf\xperf -on PROC_THREAD+LOADER+pmc_profile+profile -pmcprofile InstructionRetired -f c:\home\kernel.etl
corerun.exe %* 
c:\xperf\xperf -d c:\home\perf.etl

goto :EOF

@rem clr event variant

:CLR

c:\xperf\xperf -on PROC_THREAD+LOADER+pmc_profile+profile -pmcprofile InstructionRetired -f c:\home\kernel.etl
c:\xperf\xperf -start clr -on e13c0d23-ccbc-4e12-931b-d9cc2eee27e4:0x1CCBD:0x5 -f c:\home\clr.etl
corerun.exe %* 
c:\xperf\xperf -stop clr
c:\xperf\xperf -stop
c:\xperf\xperf -merge c:\home\clr.etl c:\home\kernel.etl c:\home\perf.etl




