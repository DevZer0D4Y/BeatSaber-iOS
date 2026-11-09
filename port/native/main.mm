// Single-binary entry point: the UnityFramework class (trampoline Classes/main.mm) is linked
// into the app executable instead of being loaded from Frameworks/UnityFramework.framework.
#include <UnityFramework/UnityFramework.h>
#include <mach-o/ldsyms.h>

int main(int argc, char* argv[])
{
    @autoreleasepool
    {
        UnityFramework* ufw = [UnityFramework getInstance];
        [ufw setExecuteHeader: &_mh_execute_header];
        [ufw runUIApplicationMainWithArgc: argc argv: argv];
        return 0;
    }
}
