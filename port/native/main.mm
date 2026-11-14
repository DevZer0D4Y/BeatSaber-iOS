// Single-binary entry point: the UnityFramework class (trampoline Classes/main.mm) is linked
// into the app executable instead of being loaded from Frameworks/UnityFramework.framework.
//
// The app contains no game files. Until the player has added theirs (see GameData.mm), it
// shows how to do that instead of starting Unity.
#include <UnityFramework/UnityFramework.h>
#include <mach-o/ldsyms.h>
#import <UIKit/UIKit.h>

// GameData.mm is only part of the game-free build; without it the game data is in the bundle.
extern "C" NSArray<NSString*>* GameDataMissing() __attribute__((weak_import));
extern "C" NSString* GameRootDir() __attribute__((weak_import));

@interface MissingFilesDelegate : UIResponder<UIApplicationDelegate>
@property (strong, nonatomic) UIWindow* window;
@property (strong, nonatomic) UILabel* label;
@end

@implementation MissingFilesDelegate
- (BOOL)application:(UIApplication*)app didFinishLaunchingWithOptions:(NSDictionary*)opts
{
    [[NSFileManager defaultManager] createDirectoryAtPath: [GameRootDir() stringByAppendingPathComponent: @"Data"]
                              withIntermediateDirectories: YES attributes: nil error: nil];
    self.window = [[UIWindow alloc] initWithFrame: [UIScreen mainScreen].bounds];
    UIViewController* vc = [UIViewController new];
    vc.view.backgroundColor = [UIColor blackColor];
    self.label = [UILabel new];
    self.label.numberOfLines = 0;
    self.label.textColor = [UIColor whiteColor];
    self.label.font = [UIFont systemFontOfSize: 17];
    self.label.translatesAutoresizingMaskIntoConstraints = NO;
    UIScrollView* scroll = [UIScrollView new];
    scroll.translatesAutoresizingMaskIntoConstraints = NO;
    [vc.view addSubview: scroll];
    [scroll addSubview: self.label];
    UILayoutGuide* g = vc.view.safeAreaLayoutGuide;
    [NSLayoutConstraint activateConstraints: @[
        [scroll.leadingAnchor constraintEqualToAnchor: g.leadingAnchor constant: 24],
        [scroll.trailingAnchor constraintEqualToAnchor: g.trailingAnchor constant: -24],
        [scroll.topAnchor constraintEqualToAnchor: g.topAnchor constant: 16],
        [scroll.bottomAnchor constraintEqualToAnchor: g.bottomAnchor constant: -16],
        [self.label.leadingAnchor constraintEqualToAnchor: scroll.leadingAnchor],
        [self.label.trailingAnchor constraintEqualToAnchor: scroll.trailingAnchor],
        [self.label.topAnchor constraintEqualToAnchor: scroll.topAnchor],
        [self.label.bottomAnchor constraintEqualToAnchor: scroll.bottomAnchor],
        [self.label.widthAnchor constraintEqualToAnchor: scroll.widthAnchor],
    ]];
    self.window.rootViewController = vc;
    [self.window makeKeyAndVisible];
    [self refresh];
    [NSTimer scheduledTimerWithTimeInterval: 2 repeats: YES block: ^(NSTimer* t) { [self refresh]; }];
    return YES;
}

- (void)refresh
{
    NSArray* missing = GameDataMissing();
    if (missing.count == 0)
    {
        self.label.text = @"Your game files are in place.\n\nClose the app (swipe it away in the app switcher) and open it again to start.";
        return;
    }
    self.label.text = [NSString stringWithFormat:
        @"Beat Saber needs your own game files.\n\n"
        "This app does not include the game. Convert your copy of Beat Saber 1.29.1 (Steam) on a computer "
        "with the import tool from the project's README, then copy the resulting \"Data\" folder into:\n\n"
        "Files › On My iPhone › Beat Saber › GameData\n\n"
        "(from a computer: the iPhone's Files tab in Finder or the Apple Devices app).\n\n"
        "Still missing: %@", [missing componentsJoinedByString: @", "]];
}
@end

int main(int argc, char* argv[])
{
    @autoreleasepool
    {
        if (GameDataMissing && GameDataMissing().count > 0)
            return UIApplicationMain(argc, argv, nil, NSStringFromClass([MissingFilesDelegate class]));
        UnityFramework* ufw = [UnityFramework getInstance];
        [ufw setExecuteHeader: &_mh_execute_header];
        [ufw runUIApplicationMainWithArgc: argc argv: argv];
        return 0;
    }
}
