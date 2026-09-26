#import <Foundation/Foundation.h>
#import <MediaPlayer/MediaPlayer.h>
#import <dispatch/dispatch.h>
#include <math.h>
#include <pthread.h>
#include <time.h>
#include "musicbridge_media.h"

static pthread_mutex_t g_gate = PTHREAD_MUTEX_INITIALIZER;
static mb_media_command g_queue[64];
static unsigned g_head, g_count;
static uint64_t g_sequence, g_epoch, g_track_token;
static uint64_t g_received, g_accepted, g_rejected;
static int32_t g_active, g_capabilities, g_registered;
static MPRemoteCommand *g_commands[6];
static id g_tokens[6];
static NSDictionary *g_last_info;

static double monotonic_seconds(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (double)ts.tv_sec + (double)ts.tv_nsec / 1000000000.0;
}

static void on_main(dispatch_block_t block) {
    if ([NSThread isMainThread]) block();
    else dispatch_async(dispatch_get_main_queue(), block);
}

static MPRemoteCommandHandlerStatus received(int type, double seek) {
    pthread_mutex_lock(&g_gate);
    g_received++;
    int flag = type == MB_MEDIA_PLAY ? 1 : type == MB_MEDIA_PAUSE ? 2 :
        type == MB_MEDIA_NEXT ? 4 : type == MB_MEDIA_PREVIOUS ? 8 : type == MB_MEDIA_SEEK ? 16 : 3;
    if (!g_active || !(g_capabilities & flag)) {
        g_rejected++;
        pthread_mutex_unlock(&g_gate);
        return MPRemoteCommandHandlerStatusNoActionableNowPlayingItem;
    }
    if (g_count == 64 || (type == MB_MEDIA_SEEK && (!isfinite(seek) || seek < 0))) {
        g_rejected++;
        pthread_mutex_unlock(&g_gate);
        return MPRemoteCommandHandlerStatusCommandFailed;
    }
    unsigned tail = (g_head + g_count) % 64;
    g_queue[tail] = (mb_media_command){ MB_MEDIA_ABI, (uint32_t)sizeof(mb_media_command),
        ++g_sequence, g_epoch, g_track_token, type, 0, seek, monotonic_seconds(), 0 };
    g_count++;
    g_accepted++;
    pthread_mutex_unlock(&g_gate);
    return MPRemoteCommandHandlerStatusSuccess; // accepted, not yet executed by Unity
}

static void register_targets(void) {
    if (g_registered) return;
    MPRemoteCommandCenter *center = [MPRemoteCommandCenter sharedCommandCenter];
    g_commands[0] = center.playCommand;
    g_commands[1] = center.pauseCommand;
    g_commands[2] = center.togglePlayPauseCommand;
    g_commands[3] = center.nextTrackCommand;
    g_commands[4] = center.previousTrackCommand;
    g_commands[5] = center.changePlaybackPositionCommand;
    int types[6] = { MB_MEDIA_PLAY, MB_MEDIA_PAUSE, MB_MEDIA_TOGGLE,
        MB_MEDIA_NEXT, MB_MEDIA_PREVIOUS, MB_MEDIA_SEEK };
    for (int i = 0; i < 6; i++) {
        int type = types[i];
        g_tokens[i] = [g_commands[i] addTargetWithHandler:^MPRemoteCommandHandlerStatus(MPRemoteCommandEvent *event) {
            double seek = 0;
            if (type == MB_MEDIA_SEEK && [event isKindOfClass:[MPChangePlaybackPositionCommandEvent class]])
                seek = ((MPChangePlaybackPositionCommandEvent *)event).positionTime;
            return received(type, seek);
        }];
    }
    pthread_mutex_lock(&g_gate);
    g_registered = 6;
    pthread_mutex_unlock(&g_gate);
}

static void unregister_targets(void) {
    for (int i = 0; i < 6; i++) {
        if (g_tokens[i] != nil) [g_commands[i] removeTarget:g_tokens[i]];
        g_tokens[i] = nil;
        g_commands[i] = nil;
    }
    pthread_mutex_lock(&g_gate);
    g_registered = 0;
    pthread_mutex_unlock(&g_gate);
}

static void clear_ours(void) {
    MPNowPlayingInfoCenter *center = [MPNowPlayingInfoCenter defaultCenter];
    if (g_last_info != nil && [center.nowPlayingInfo isEqualToDictionary:g_last_info]) {
        center.nowPlayingInfo = nil;
        center.playbackState = MPNowPlayingPlaybackStateStopped;
    }
    g_last_info = nil;
}

uint32_t mb_media_abi_version(void) { return MB_MEDIA_ABI; }

int32_t mb_media_initialize(void) {
    if (@available(macOS 10.12.2, *)) return 1;
    return 0;
}

int32_t mb_media_publish(const mb_media_snapshot *snapshot) {
    if (snapshot == NULL || snapshot->abi != MB_MEDIA_ABI ||
        snapshot->size != sizeof(mb_media_snapshot) || snapshot->title == NULL ||
        !isfinite(snapshot->position_seconds) || !isfinite(snapshot->duration_seconds) ||
        snapshot->position_seconds < 0 || snapshot->duration_seconds < 0) return 0;
    @autoreleasepool {
        NSString *title = [NSString stringWithUTF8String:snapshot->title];
        NSString *artist = snapshot->artist ? [NSString stringWithUTF8String:snapshot->artist] : @"";
        if (title == nil || title.length == 0) return 0;
        if (artist == nil) artist = @"";
        uint64_t epoch = snapshot->owner_epoch;
        int state = snapshot->state;
        int caps = snapshot->capabilities;
        double position = snapshot->position_seconds;
        double duration = snapshot->duration_seconds;
        pthread_mutex_lock(&g_gate);
        g_active = 1; g_epoch = epoch; g_track_token = snapshot->track_token; g_capabilities = caps;
        pthread_mutex_unlock(&g_gate);
        on_main(^{
            pthread_mutex_lock(&g_gate);
            BOOL current = g_active && g_epoch == epoch;
            pthread_mutex_unlock(&g_gate);
            if (!current) return;
            register_targets();
            for (int i = 0; i < 6; i++) {
                int flag = i == 0 ? 1 : i == 1 ? 2 : i == 2 ? 3 : i == 3 ? 4 : i == 4 ? 8 : 16;
                g_commands[i].enabled = (caps & flag) != 0;
            }
            NSMutableDictionary *info = [NSMutableDictionary dictionary];
            info[MPMediaItemPropertyTitle] = title;
            if (artist.length > 0) info[MPMediaItemPropertyArtist] = artist;
            if (duration > 0) info[MPMediaItemPropertyPlaybackDuration] = @(duration);
            info[MPNowPlayingInfoPropertyElapsedPlaybackTime] = @(position);
            info[MPNowPlayingInfoPropertyPlaybackRate] = @(state == 1 ? 1.0 : 0.0);
            MPNowPlayingInfoCenter *center = [MPNowPlayingInfoCenter defaultCenter];
            center.nowPlayingInfo = info;
            center.playbackState = state == 1 ? MPNowPlayingPlaybackStatePlaying : MPNowPlayingPlaybackStatePaused;
            g_last_info = [info copy];
        });
    }
    return 1;
}

void mb_media_deactivate(uint64_t owner_epoch) {
    pthread_mutex_lock(&g_gate);
    if (g_active && g_epoch == owner_epoch) {
        g_active = 0; g_capabilities = 0; g_count = 0; g_head = 0;
        pthread_mutex_unlock(&g_gate);
        on_main(^{ unregister_targets(); clear_ours(); });
        return;
    }
    pthread_mutex_unlock(&g_gate);
}

int32_t mb_media_poll(mb_media_command *out_command) {
    if (out_command == NULL) return 0;
    pthread_mutex_lock(&g_gate);
    if (g_count == 0) { pthread_mutex_unlock(&g_gate); return 0; }
    *out_command = g_queue[g_head];
    out_command->age_seconds = monotonic_seconds() - out_command->received_monotonic_seconds;
    g_head = (g_head + 1) % 64; g_count--;
    pthread_mutex_unlock(&g_gate);
    return 1;
}

int32_t mb_media_registered_target_count(void) {
    pthread_mutex_lock(&g_gate);
    int count = g_registered;
    pthread_mutex_unlock(&g_gate);
    return count;
}

int32_t mb_media_get_diagnostics(mb_media_diagnostics *out_diagnostics) {
    if (out_diagnostics == NULL || out_diagnostics->abi != MB_MEDIA_ABI ||
        out_diagnostics->size != sizeof(mb_media_diagnostics)) return 0;
    pthread_mutex_lock(&g_gate);
    *out_diagnostics = (mb_media_diagnostics){ MB_MEDIA_ABI,
        (uint32_t)sizeof(mb_media_diagnostics), g_received, g_accepted, g_rejected,
        g_count, (uint32_t)g_registered };
    pthread_mutex_unlock(&g_gate);
    return 1;
}

void mb_media_shutdown(void) {
    pthread_mutex_lock(&g_gate);
    g_active = 0; g_capabilities = 0; g_count = 0; g_head = 0;
    pthread_mutex_unlock(&g_gate);
    on_main(^{ unregister_targets(); clear_ours(); });
}
