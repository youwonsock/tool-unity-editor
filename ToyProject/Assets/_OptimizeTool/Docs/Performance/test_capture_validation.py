import csv
import gzip
import hashlib
import io
import json
import shutil
from pathlib import Path
import tempfile
import unittest

from capture_validation import load_capture
from analyze_benchmark import analyze


class CaptureIntegrityTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.folder = Path(self.temporary.name)
        self.meta = {'schemaVersion': 2, 'captureId': 'test-capture', 'rounds': 1, 'samplesPerBlock': 1, 'offscreen': True}
        stream = io.StringIO(newline='')
        writer = csv.DictWriter(stream, fieldnames=['round', 'mode', 'metal_frame_id', 'metal_gpu_ms', 'timing_timestamp', 'cpu_frame_ms', 'gpu_frame_ms', 'focused'])
        writer.writeheader()
        for mode in range(4):
            writer.writerow(dict(round=0, mode=mode, metal_frame_id=mode + 1, metal_gpu_ms=1, timing_timestamp=0, cpu_frame_ms=0, gpu_frame_ms=0, focused=0))
        self.payload = stream.getvalue().encode()
        self.metadata = json.dumps(self.meta).encode()
        self.completion = {'schemaVersion': 2, 'captureId': 'test-capture', 'status': 'complete', 'samples': 4,
                           'framesSha256': hashlib.sha256(self.payload).hexdigest(), 'metadataSha256': hashlib.sha256(self.metadata).hexdigest()}
        (self.folder / 'metadata.json').write_bytes(self.metadata)
        (self.folder / 'frames.csv').write_bytes(self.payload)
        (self.folder / 'capture.json').write_text(json.dumps({'schemaVersion': 2, 'captureId': 'test-capture'}))
        self.write_completion()

    def tearDown(self):
        self.temporary.cleanup()

    def write_completion(self):
        (self.folder / 'complete.json').write_text(json.dumps(self.completion))

    def test_complete_and_compressed_capture_are_valid(self):
        self.assertEqual(4, len(load_capture(self.folder)[1]))
        (self.folder / 'frames.csv').unlink()
        (self.folder / 'frames.csv.gz').write_bytes(gzip.compress(self.payload))
        self.assertEqual(4, len(load_capture(self.folder)[1]))

    def test_error_marker_rejects_stale_complete_data_in_analyzer(self):
        (self.folder / 'error.txt').write_text('interrupted')
        with self.assertRaisesRegex(ValueError, 'Failed capture'):
            analyze([self.folder])

    def test_missing_completion_is_rejected(self):
        (self.folder / 'complete.json').unlink()
        with self.assertRaises(FileNotFoundError):
            load_capture(self.folder)

    def test_same_capture_cannot_be_counted_as_two_independent_runs(self):
        with self.assertRaisesRegex(ValueError, 'directory was supplied twice'):
            analyze([self.folder, self.folder])
        with tempfile.TemporaryDirectory() as another:
            copy = Path(another) / 'copy'
            shutil.copytree(self.folder, copy)
            with self.assertRaisesRegex(ValueError, 'identity was supplied twice'):
                analyze([self.folder, copy])

    def test_nonfinite_timings_are_rejected_even_with_a_matching_hash(self):
        payload = self.payload.replace(b',1,0,0,0,0', b',NaN,0,0,0,0', 1)
        self.assertNotEqual(payload, self.payload)
        (self.folder / 'frames.csv').write_bytes(payload)
        self.completion['framesSha256'] = hashlib.sha256(payload).hexdigest()
        self.write_completion()
        with self.assertRaisesRegex(ValueError, 'Non-finite'):
            load_capture(self.folder)

    def test_mixed_capture_identity_is_rejected(self):
        self.completion['captureId'] = 'another-run'
        self.write_completion()
        with self.assertRaisesRegex(ValueError, 'identity mismatch'):
            load_capture(self.folder)

    def test_changed_frames_or_metadata_are_rejected(self):
        for name in ('frames.csv', 'metadata.json'):
            with self.subTest(name=name):
                path = self.folder / name
                before = path.read_bytes()
                path.write_bytes(before + b'\n')
                with self.assertRaisesRegex(ValueError, 'hash mismatch'):
                    load_capture(self.folder)
                path.write_bytes(before)

    def test_missing_or_unsupported_schema_is_rejected(self):
        for version in (None, 1, 3):
            with self.subTest(version=version):
                metadata = dict(self.meta); completion = dict(self.completion)
                if version is None:
                    metadata.pop('schemaVersion'); completion.pop('schemaVersion')
                else:
                    metadata['schemaVersion'] = completion['schemaVersion'] = version
                (self.folder / 'metadata.json').write_text(json.dumps(metadata))
                (self.folder / 'complete.json').write_text(json.dumps(completion))
                with self.assertRaisesRegex(ValueError, 'only v2'):
                    load_capture(self.folder)

    def test_v2_requires_its_claim_file(self):
        (self.folder / 'capture.json').unlink()
        with self.assertRaises(FileNotFoundError):
            load_capture(self.folder)

    def test_claim_schema_must_match_v2(self):
        (self.folder / 'capture.json').write_text(json.dumps({'schemaVersion': 1, 'captureId': 'test-capture'}))
        with self.assertRaisesRegex(ValueError, 'only v2'):
            load_capture(self.folder)


if __name__ == '__main__':
    unittest.main()
