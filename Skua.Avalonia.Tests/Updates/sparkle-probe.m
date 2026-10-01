// Asks Sparkle, as a release of Skua would, whether the appcast at <feed-url> has an update for the bundle at <bundle>, and prints one line:
// "found <build> <version>", "none" or "error <code> <description>". It shows nothing: its user driver is this headless one.
// AppUpdatesTests compiles it against the pinned Sparkle. Usage: sparkle-probe <bundle> <feed-url>
#import <Foundation/Foundation.h>
#import <Sparkle/Sparkle.h>

static void finish(NSString *line)
{
    printf("%s\n", line.UTF8String);
    fflush(stdout);
    exit(0);
}

@interface Probe : NSObject <SPUUserDriver, SPUUpdaterDelegate>
@property (copy) NSString *feed;
@end

@implementation Probe
- (NSString *)feedURLStringForUpdater:(SPUUpdater *)updater { return self.feed; }
- (void)updater:(SPUUpdater *)updater didFindValidUpdate:(SUAppcastItem *)item
{
    finish([NSString stringWithFormat:@"found %@ %@", item.versionString, item.displayVersionString]);
}
- (void)updaterDidNotFindUpdate:(SPUUpdater *)updater error:(NSError *)error { finish(@"none"); }
- (void)updater:(SPUUpdater *)updater didAbortWithError:(NSError *)error
{
    finish([NSString stringWithFormat:@"error %ld %@", (long)error.code, error.localizedDescription]);
}
- (void)showUpdatePermissionRequest:(SPUUpdatePermissionRequest *)request reply:(void (^)(SUUpdatePermissionResponse *))reply { finish(@"asked permission"); }
- (void)showUserInitiatedUpdateCheckWithCancellation:(void (^)(void))cancellation {}
- (void)showUpdateFoundWithAppcastItem:(SUAppcastItem *)appcastItem state:(SPUUserUpdateState *)state reply:(void (^)(SPUUserUpdateChoice))reply {}
- (void)showUpdateReleaseNotesWithDownloadData:(SPUDownloadData *)downloadData {}
- (void)showUpdateReleaseNotesFailedToDownloadWithError:(NSError *)error {}
- (void)showUpdateNotFoundWithError:(NSError *)error acknowledgement:(void (^)(void))acknowledgement {}
- (void)showUpdaterError:(NSError *)error acknowledgement:(void (^)(void))acknowledgement {}
- (void)showDownloadInitiatedWithCancellation:(void (^)(void))cancellation {}
- (void)showDownloadDidReceiveExpectedContentLength:(uint64_t)expectedContentLength {}
- (void)showDownloadDidReceiveDataOfLength:(uint64_t)length {}
- (void)showDownloadDidStartExtractingUpdate {}
- (void)showExtractionReceivedProgress:(double)progress {}
- (void)showReadyToInstallAndRelaunch:(void (^)(SPUUserUpdateChoice))reply {}
- (void)showInstallingUpdateWithApplicationTerminated:(BOOL)applicationTerminated retryTerminatingApplication:(void (^)(void))retryTerminatingApplication {}
- (void)showUpdateInstalledAndRelaunched:(BOOL)relaunched acknowledgement:(void (^)(void))acknowledgement {}
- (void)dismissUpdateInstallation {}
- (void)showUpdateInFocus {}
@end

int main(int argc, const char *argv[])
{
    @autoreleasepool {
        if (argc != 3) {
            fprintf(stderr, "usage: sparkle-probe <bundle> <feed-url>\n");
            return 2;
        }
        NSBundle *bundle = [NSBundle bundleWithPath:@(argv[1])];
        Probe *probe = [Probe new];
        probe.feed = @(argv[2]);
        SPUUpdater *updater = [[SPUUpdater alloc] initWithHostBundle:bundle applicationBundle:bundle userDriver:probe delegate:probe];
        NSError *error = nil;
        if (![updater startUpdater:&error])
            finish([NSString stringWithFormat:@"error %ld %@", (long)error.code, error.localizedDescription]);
        [updater checkForUpdateInformation];
        [[NSRunLoop mainRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:60]];
        finish(@"timeout");
    }
}
