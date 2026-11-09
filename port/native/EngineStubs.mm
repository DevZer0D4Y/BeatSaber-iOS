// Symbols referenced by engine code paths the game never uses (Enlighten GI debug tasks),
// which a stock Unity export resolves elsewhere. Return types are not part of the
// mangled names; these are only here so the link succeeds.
#import <Foundation/Foundation.h>

namespace Geo
{
    const char* GetiOSAppDocumentsDir()
    {
        static char path[1024];
        if (!path[0])
        {
            NSString* d = NSSearchPathForDirectoriesInDomains(NSDocumentDirectory, NSUserDomainMask, YES).firstObject;
            strlcpy(path, d ? d.fileSystemRepresentation : "", sizeof(path));
        }
        return path;
    }
}

namespace Enlighten
{
    struct RadDebugFormFactorTask
    {
        void* GetOutputHeader();
        void* GetOutputSystemIds();
        void* GetOutputClusterIds();
        void* GetOutputFormFactors();
    };
    void* RadDebugFormFactorTask::GetOutputHeader() { return nullptr; }
    void* RadDebugFormFactorTask::GetOutputSystemIds() { return nullptr; }
    void* RadDebugFormFactorTask::GetOutputClusterIds() { return nullptr; }
    void* RadDebugFormFactorTask::GetOutputFormFactors() { return nullptr; }
}
