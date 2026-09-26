import ctypes
import pathlib
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]
LIB = ROOT / ".downloads/media-native/libmusicbridge_media.dylib"


class Snapshot(ctypes.Structure):
    _fields_ = [
        ("abi", ctypes.c_uint32), ("size", ctypes.c_uint32),
        ("owner_epoch", ctypes.c_uint64), ("track_token", ctypes.c_uint64),
        ("state", ctypes.c_int32), ("capabilities", ctypes.c_int32),
        ("position_seconds", ctypes.c_double), ("duration_seconds", ctypes.c_double),
        ("title", ctypes.c_char_p), ("artist", ctypes.c_char_p),
    ]


class Command(ctypes.Structure):
    _fields_ = [
        ("abi", ctypes.c_uint32), ("size", ctypes.c_uint32),
        ("sequence", ctypes.c_uint64), ("owner_epoch", ctypes.c_uint64),
        ("track_token", ctypes.c_uint64), ("type", ctypes.c_int32),
        ("reserved", ctypes.c_int32), ("seek_seconds", ctypes.c_double),
        ("received_monotonic_seconds", ctypes.c_double), ("age_seconds", ctypes.c_double),
    ]


@unittest.skipUnless(LIB.exists(), "build-media-native.sh has not run")
class MediaNativeTests(unittest.TestCase):
    def test_abi_and_inactive_lifecycle(self):
        self.assertEqual(ctypes.sizeof(Snapshot), 64)
        self.assertEqual(ctypes.sizeof(Command), 64)
        lib = ctypes.CDLL(str(LIB))
        lib.mb_media_abi_version.restype = ctypes.c_uint32
        lib.mb_media_initialize.restype = ctypes.c_int32
        lib.mb_media_registered_target_count.restype = ctypes.c_int32
        lib.mb_media_poll.argtypes = [ctypes.POINTER(Command)]
        lib.mb_media_poll.restype = ctypes.c_int32
        lib.mb_media_publish.argtypes = [ctypes.POINTER(Snapshot)]
        lib.mb_media_publish.restype = ctypes.c_int32
        self.assertEqual(lib.mb_media_abi_version(), 1)
        self.assertEqual(lib.mb_media_initialize(), 1)
        self.assertEqual(lib.mb_media_registered_target_count(), 0)
        self.assertEqual(lib.mb_media_poll(ctypes.byref(Command())), 0)
        bad = Snapshot(abi=1, size=0, title=b"invalid")
        self.assertEqual(lib.mb_media_publish(ctypes.byref(bad)), 0)
        self.assertEqual(lib.mb_media_registered_target_count(), 0)
        lib.mb_media_shutdown()


if __name__ == "__main__":
    unittest.main()
