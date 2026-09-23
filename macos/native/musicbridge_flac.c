#define DR_FLAC_IMPLEMENTATION
#define DR_FLAC_NO_OGG
#include "vendor/dr_flac.h"
#include <stdint.h>

#define MB_API __attribute__((visibility("default")))
/* Stable ABI. No dr_flac structs or callbacks cross the managed boundary. */
MB_API int mb_flac_abi(void) { return 1; }
MB_API void* mb_flac_open(const char* path) {
    drflac* f = drflac_open_file(path, NULL);
    if (!f) return NULL;
    if (f->channels < 1 || f->channels > 2 ||
        (f->bitsPerSample != 16 && f->bitsPerSample != 24) ||
        f->sampleRate < 8000 || f->sampleRate > 192000 ||
        f->totalPCMFrameCount == 0 || f->totalPCMFrameCount > INT32_MAX) {
        drflac_close(f); return NULL;
    }
    return f;
}
MB_API int mb_flac_info(void* handle, int* rate, int* channels, int* bits, uint64_t* frames) {
    drflac* f = handle;
    if (!f || !rate || !channels || !bits || !frames) return 0;
    *rate = f->sampleRate; *channels = f->channels; *bits = f->bitsPerSample;
    *frames = f->totalPCMFrameCount; return 1;
}
MB_API uint64_t mb_flac_read(void* handle, uint64_t frames, float* samples) {
    if (!handle || !samples) return 0;
    return drflac_read_pcm_frames_f32(handle, frames, samples);
}
MB_API int mb_flac_seek(void* handle, uint64_t frame) {
    return handle && drflac_seek_to_pcm_frame(handle, frame);
}
MB_API void mb_flac_close(void* handle) { if (handle) drflac_close(handle); }
