#ifndef MUSICBRIDGE_MEDIA_H
#define MUSICBRIDGE_MEDIA_H
#include <stdint.h>

#define MB_MEDIA_ABI 2u
#define MB_MEDIA_PLAY 1
#define MB_MEDIA_PAUSE 2
#define MB_MEDIA_TOGGLE 3
#define MB_MEDIA_NEXT 4
#define MB_MEDIA_PREVIOUS 5
#define MB_MEDIA_SEEK 6
#define MB_MEDIA_EXPORT __attribute__((visibility("default")))

// All pointers are borrowed for the call only. Strings are UTF-8 and copied.
typedef struct mb_media_snapshot {
    uint32_t abi;
    uint32_t size;
    uint64_t owner_epoch;
    uint64_t track_token;
    int32_t state;       // 1 playing, 2 paused/loading
    int32_t capabilities; // play=1, pause=2, next=4, previous=8, seek=16
    double position_seconds;
    double duration_seconds;
    const char *title;
    const char *artist;
} mb_media_snapshot;

typedef struct mb_media_command {
    uint32_t abi;
    uint32_t size;
    uint64_t sequence;
    uint64_t owner_epoch;
    uint64_t track_token;
    int32_t type;
    int32_t reserved;
    double seek_seconds;
    double received_monotonic_seconds;
    double age_seconds;
} mb_media_command;

typedef struct mb_media_diagnostics {
    uint32_t abi;
    uint32_t size;
    uint64_t received;
    uint64_t accepted;
    uint64_t rejected;
    uint32_t queue_depth;
    uint32_t registered_targets;
} mb_media_diagnostics;

#ifdef __cplusplus
extern "C" {
#endif
MB_MEDIA_EXPORT uint32_t mb_media_abi_version(void);
MB_MEDIA_EXPORT int32_t mb_media_initialize(void);
MB_MEDIA_EXPORT int32_t mb_media_publish(const mb_media_snapshot *snapshot);
MB_MEDIA_EXPORT void mb_media_deactivate(uint64_t owner_epoch);
MB_MEDIA_EXPORT int32_t mb_media_poll(mb_media_command *out_command);
MB_MEDIA_EXPORT int32_t mb_media_registered_target_count(void);
MB_MEDIA_EXPORT int32_t mb_media_get_diagnostics(mb_media_diagnostics *out_diagnostics);
MB_MEDIA_EXPORT void mb_media_shutdown(void);
#ifdef __cplusplus
}
#endif
#endif
