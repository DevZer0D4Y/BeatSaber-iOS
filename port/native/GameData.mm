// The app ships without any game files: the player supplies them. This file tells Unity and
// IL2CPP where everything is.
//
//   Documents/GameData/Data/          the player's converted game data (Files app > Beat Saber)
//   Documents/GameData/Data/Managed/  the player's patched game assemblies (interpreted)
//   <app>/Il2CppData/                 IL2CPP metadata for the compiled-in .NET/Unity code
//   <app>/HybridCLR/aot, hot          HybridCLR metadata and the port's own assembly
//   <app>/unity default resources     Unity's built-in resources, copied into Data on start
//
// UnityDataBundleDir() replaces the trampoline's (Classes/Unity/Filesystem.mm is compiled with
// -DUnityDataBundleDir=UnityDataBundleDir_trampoline). When the game files are missing, main()
// shows how to add them instead of starting Unity (see GameDataMissing()).
#import <Foundation/Foundation.h>
#include <string>
#include <sys/xattr.h>

static NSString* BundleDir() { return [[NSBundle mainBundle] bundlePath]; }

static NSString* DocumentsDir()
{
    return NSSearchPathForDirectoriesInDomains(NSDocumentDirectory, NSUserDomainMask, YES)[0];
}

extern "C" NSString* GameRootDir() { return [DocumentsDir() stringByAppendingPathComponent: @"GameData"]; }
static NSString* GameDataDir() { return [GameRootDir() stringByAppendingPathComponent: @"Data"]; }

// The things a converted game folder must contain.
extern "C" NSArray<NSString*>* GameDataMissing()
{
    NSFileManager* fm = [NSFileManager defaultManager];
    NSMutableArray* missing = [NSMutableArray array];
    for (NSString* rel in @[@"globalgamemanagers", @"level0", @"Managed/Main.dll", @"Managed/HMLib.dll"])
        if (![fm fileExistsAtPath: [GameDataDir() stringByAppendingPathComponent: rel]]) [missing addObject: rel];
    return missing;
}

static void CopyIfDifferent(NSString* src, NSString* dst)
{
    NSFileManager* fm = [NSFileManager defaultManager];
    NSDictionary* a = [fm attributesOfItemAtPath: src error: nil];
    NSDictionary* b = [fm attributesOfItemAtPath: dst error: nil];
    if (a && b && [a fileSize] == [b fileSize]) return;
    [fm removeItemAtPath: dst error: nil];
    [fm copyItemAtPath: src toPath: dst error: nil];
}

static void PrepareGameData()
{
    NSString* root = GameRootDir();
    [[NSFileManager defaultManager] createDirectoryAtPath: GameDataDir() withIntermediateDirectories: YES attributes: nil error: nil];
    u_int8_t one = 1;   // 2 GB of game data: keep it out of iCloud/computer backups
    setxattr([root fileSystemRepresentation], "com.apple.MobileBackup", &one, 1, 0, 0);
    CopyIfDifferent([BundleDir() stringByAppendingPathComponent: @"unity default resources"],
                    [GameDataDir() stringByAppendingPathComponent: @"unity default resources"]);
}

extern "C" const char* UnityDataBundleDir()
{
    static const char* dir = NULL;
    if (dir == NULL)
    {
        PrepareGameData();
        dir = strdup([GameRootDir() fileSystemRepresentation]);
    }
    return dir;
}

// IL2CPP's data folder (global-metadata.dat etc.) stays inside the app.
extern "C" const char* hclr_port_il2cpp_data_dir()
{
    static const char* dir = strdup([[BundleDir() stringByAppendingPathComponent: @"Il2CppData"] fileSystemRepresentation]);
    return dir;
}

extern "C" bool hclr_port_dirs(std::string* aotDir, std::string* hotDirs)
{
    NSString* hc = [BundleDir() stringByAppendingPathComponent: @"HybridCLR"];
    *aotDir = [[hc stringByAppendingPathComponent: @"aot"] fileSystemRepresentation];
    *hotDirs = std::string([[hc stringByAppendingPathComponent: @"hot"] fileSystemRepresentation]) + ":" +
               [[GameDataDir() stringByAppendingPathComponent: @"Managed"] fileSystemRepresentation];
    return true;
}
