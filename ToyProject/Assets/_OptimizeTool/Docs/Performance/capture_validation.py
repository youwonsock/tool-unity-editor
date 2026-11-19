"""Validate capture identity, payload hashes and complete mode blocks."""
import csv
import hashlib
import io
import json
import math

SCHEMA_VERSION = 2


def load_capture(folder):
    if (folder / 'error.txt').exists():
        raise ValueError(f'Failed capture: {folder}')
    metadata_bytes = (folder / 'metadata.json').read_bytes()
    metadata = json.loads(metadata_bytes)
    completion = json.loads((folder / 'complete.json').read_text())
    payload = (folder / 'frames.csv').read_bytes()
    rows = list(csv.DictReader(io.StringIO(payload.decode('utf-8-sig'))))
    if completion.get('status') != 'complete' or completion.get('samples') != len(rows):
        raise ValueError('Incomplete capture')
    rounds, block_size = metadata['rounds'], metadata['samplesPerBlock']
    if not (1 <= rounds <= 32 and block_size > 0) or len(rows) != rounds * 4 * block_size:
        raise ValueError('Unexpected sample count')
    claim = json.loads((folder / 'capture.json').read_text())
    if not (metadata.get('schemaVersion') == completion.get('schemaVersion') == claim.get('schemaVersion') == SCHEMA_VERSION):
        raise ValueError('Capture schema mismatch')
    if not metadata.get('captureId') or not (claim.get('captureId') == metadata['captureId'] == completion.get('captureId')):
        raise ValueError('Capture identity mismatch')
    if completion.get('framesSha256') != hashlib.sha256(payload).hexdigest():
        raise ValueError('Frame payload hash mismatch')
    if completion.get('metadataSha256') != hashlib.sha256(metadata_bytes).hexdigest():
        raise ValueError('Metadata hash mismatch')
    blocks, identities = {}, set()
    offscreen = metadata.get('offscreen', False)
    for row in rows:
        if not all(math.isfinite(float(value)) for value in row.values()):
            raise ValueError('Non-finite or malformed sample')
        round_, mode = int(row['round']), int(row['mode'])
        if float(row['round']) != round_ or float(row['mode']) != mode or not (0 <= round_ < rounds and 0 <= mode < 4):
            raise ValueError('Invalid sample block')
        blocks[round_, mode] = blocks.get((round_, mode), 0) + 1
        identity = row['metal_frame_id'] if offscreen else row['timing_timestamp']
        metric = 'metal_gpu_ms' if offscreen else 'cpu_frame_ms'
        if float(identity) <= 0 or identity in identities or float(row[metric]) <= 0:
            raise ValueError('Invalid or repeated frame timing')
        identities.add(identity)
    if len(blocks) != rounds * 4 or any(count != block_size for count in blocks.values()):
        raise ValueError('Incomplete mode block')
    return metadata, rows, completion
