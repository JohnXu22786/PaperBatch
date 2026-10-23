import hashlib
import json
import tempfile
import unittest
import warnings
import zipfile
from unittest.mock import patch
from pathlib import Path

from tools.verify_windows_package import safe_path, validate, verify_archive

class PackageVerifierTests(unittest.TestCase):
    def archive_with(self, archive, member, data=b'bad', mode=None):
        with zipfile.ZipFile(archive, 'w') as z:
            info = zipfile.ZipInfo(member)
            if mode is not None:
                info.create_system = 3
                info.external_attr = mode << 16
            z.writestr(info, data)

    def fixture(self, root):
        (root / 'app').mkdir()
        files = {
            'app/PaperBatch.Desktop.exe': b'fixture executable',
            'LICENSE': b'AGPL-3.0-only',
            'THIRD-PARTY-UI-NOTICES.md': b'UI notices',
            'THIRD-PARTY-CORE-NOTICES.md': b'Core notices',
            'DOTNET-THIRD-PARTY-NOTICES.txt': b'Runtime notices',
            'README-package.txt': b'Unsigned development beta',
        }
        for name, data in files.items():
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
        entries = [{'path': n, 'size': len(b), 'sha256': hashlib.sha256(b).hexdigest()} for n, b in sorted(files.items())]
        (root / 'package-manifest.json').write_text(json.dumps({
            'schemaVersion': 1, 'package': 'PaperBatch Windows x64 lite', 'sourceRevision': 'a' * 40,
            'target': 'win-x64', 'configuration': 'Release', 'selfContained': True, 'runtimeIdentifier': 'win-x64',
            'dependencyLockFiles': [{'path': 'src/Core/packages.lock.json', 'sha256': 'b' * 64}],
            'license': 'AGPL-3.0-only', 'signed': False, 'excludedRuntimes': ['Office', 'PDFium'], 'files': entries
        }))

    def test_manifest_and_archive_validate_and_extract_exactly(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / 'package'; root.mkdir(); self.fixture(root)
            archive = root / 'PaperBatch-windows-x64-lite.zip'
            with zipfile.ZipFile(archive, 'w') as z:
                for p in root.rglob('*'):
                    if p.is_file() and p != archive: z.write(p, p.relative_to(root).as_posix())
            (root / 'PaperBatch-windows-x64-lite.zip.sha256').write_text('0' * 64 + '  PaperBatch-windows-x64-lite.zip')
            outputs = {archive.name, archive.name + '.sha256'}
            validate(root, outputs)
            verify_archive(archive, root, outputs)

    def test_unknown_extra_output_file_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); self.fixture(root)
            (root / 'extra.zip').write_bytes(b'not an approved package output')
            with self.assertRaisesRegex(ValueError, 'file set differs'):
                validate(root, {'PaperBatch-windows-x64-lite.zip', 'PaperBatch-windows-x64-lite.zip.sha256'})

    def test_manifest_traversal_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); self.fixture(root)
            m = json.loads((root / 'package-manifest.json').read_text()); m['files'][0]['path'] = '../outside'
            (root / 'package-manifest.json').write_text(json.dumps(m))
            with self.assertRaisesRegex(ValueError, 'unsafe package path'):
                validate(root)

    def test_manifest_rejects_windows_device_and_stream_paths(self):
        for unsafe in ['app/CON.txt', 'app/file:stream', 'app/name. ', 'app/name.']:
            with self.subTest(path=unsafe), tempfile.TemporaryDirectory() as temp:
                root = Path(temp); self.fixture(root)
                m = json.loads((root / 'package-manifest.json').read_text()); m['files'][0]['path'] = unsafe
                (root / 'package-manifest.json').write_text(json.dumps(m))
                with self.assertRaisesRegex(ValueError, 'Windows-incompatible'):
                    validate(root)

    def test_rooted_manifest_path_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); self.fixture(root)
            m = json.loads((root / 'package-manifest.json').read_text()); m['files'][0]['path'] = '/outside'
            (root / 'package-manifest.json').write_text(json.dumps(m))
            with self.assertRaisesRegex(ValueError, 'unsafe package path'):
                validate(root)

    def test_windows_console_device_paths_are_rejected(self):
        for unsafe in ['app/CONIN$', 'app/CONOUT$']:
            with self.subTest(path=unsafe), self.assertRaisesRegex(ValueError, 'Windows-incompatible'):
                safe_path(unsafe)

    def test_archive_symlink_is_rejected(self):
        import stat
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / 'package'; root.mkdir(); self.fixture(root)
            archive = Path(temp) / 'symlink.zip'
            with zipfile.ZipFile(archive, 'w') as z:
                for p in root.rglob('*'):
                    if p.is_file(): z.write(p, p.relative_to(root).as_posix())
                info = zipfile.ZipInfo('app/escape.dll')
                info.create_system = 3
                info.external_attr = (stat.S_IFLNK | 0o777) << 16
                z.writestr(info, '../outside')
            with self.assertRaisesRegex(ValueError, 'symlink ZIP entry'):
                verify_archive(archive, root)

    def test_archive_tampering_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / 'package'; root.mkdir(); self.fixture(root)
            archive = Path(temp) / 'tampered.zip'
            with zipfile.ZipFile(archive, 'w') as z:
                for p in root.rglob('*'):
                    if p.is_file(): z.write(p, p.relative_to(root).as_posix())
                z.writestr('app/injected.dll', b'unexpected')
            with self.assertRaisesRegex(ValueError, 'file list differs'):
                verify_archive(archive, root)

    def test_duplicate_archive_paths_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / 'package'; root.mkdir(); self.fixture(root)
            archive = Path(temp) / 'duplicate.zip'
            with warnings.catch_warnings():
                warnings.simplefilter('ignore', UserWarning)
                with zipfile.ZipFile(archive, 'w') as z:
                    for p in root.rglob('*'):
                        if p.is_file(): z.write(p, p.relative_to(root).as_posix())
                    z.writestr('LICENSE', b'overwrite')
            with self.assertRaisesRegex(ValueError, 'duplicate ZIP entry'):
                verify_archive(archive, root)

    def test_windows_sensitive_archive_names_rejected_before_extraction(self):
        cases = ['../outside', '/rooted', 'C:/drive', 'app/file:stream', 'app/CON.txt', 'app/CONIN$', 'app/CONOUT$',
                 'app/name. ', 'app/name.', 'app/COM1.log', 'app/with?.dll']
        for member in cases:
            with self.subTest(member=member), tempfile.TemporaryDirectory() as temp:
                root = Path(temp) / 'package'; root.mkdir(); self.fixture(root)
                archive = Path(temp) / 'malicious.zip'
                self.archive_with(archive, member)
                outside = Path(temp).parent / 'outside'
                message = 'unsafe package path' if member in {'../outside', '/rooted'} else 'Windows-incompatible'
                with self.assertRaisesRegex(ValueError, message):
                    verify_archive(archive, root)
                self.assertFalse(outside.exists())
                self.assertEqual(list(Path(temp).glob('paperbatch-package-check-*')), [])

    def test_case_folded_archive_collision_is_rejected_before_extraction(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / 'package'; root.mkdir(); self.fixture(root)
            archive = Path(temp) / 'case-collision.zip'
            with zipfile.ZipFile(archive, 'w') as z:
                z.writestr('LICENSE', b'first')
                z.writestr('license', b'second')
            with self.assertRaisesRegex(ValueError, 'case-colliding'):
                verify_archive(archive, root)
            self.assertEqual(list(Path(temp).glob('paperbatch-package-check-*')), [])

    def test_special_file_metadata_is_rejected_before_extraction(self):
        import stat
        for file_type, expected in [(stat.S_IFLNK, 'symlink'), (stat.S_IFIFO, 'special-file'), (stat.S_IFCHR, 'special-file')]:
            with self.subTest(file_type=file_type), tempfile.TemporaryDirectory() as temp:
                root = Path(temp) / 'package'; root.mkdir(); self.fixture(root)
                archive = Path(temp) / 'special.zip'
                self.archive_with(archive, 'app/escape.dll', b'content', file_type | 0o777)
                with self.assertRaisesRegex(ValueError, expected):
                    verify_archive(archive, root)
                self.assertEqual(list(Path(temp).glob('paperbatch-package-check-*')), [])

    def test_oversized_declared_member_is_rejected_before_extraction(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / 'package'; root.mkdir(); self.fixture(root)
            archive = Path(temp) / 'oversized.zip'
            with zipfile.ZipFile(archive, 'w') as z:
                z.writestr('app/large.dll', b'0123456789')
            with patch('tools.verify_windows_package.MAX_MEMBER_BYTES', 5):
                with self.assertRaisesRegex(ValueError, 'size limit'):
                    verify_archive(archive, root)
            self.assertEqual(list(Path(temp).glob('paperbatch-package-check-*')), [])

    def test_total_uncompressed_archive_size_is_bounded_before_extraction(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / 'package'; root.mkdir(); self.fixture(root)
            archive = Path(temp) / 'total-oversized.zip'
            with zipfile.ZipFile(archive, 'w') as z:
                z.writestr('app/a.dll', b'1234')
                z.writestr('app/b.dll', b'5678')
            with patch('tools.verify_windows_package.MAX_TOTAL_BYTES', 7):
                with self.assertRaisesRegex(ValueError, 'total uncompressed size limit'):
                    verify_archive(archive, root)
            self.assertEqual(list(Path(temp).glob('paperbatch-package-check-*')), [])

    def test_archive_entry_count_is_bounded_before_extraction(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / 'package'; root.mkdir(); self.fixture(root)
            archive = Path(temp) / 'too-many-entries.zip'
            with zipfile.ZipFile(archive, 'w') as z:
                z.writestr('app/a.dll', b'a')
                z.writestr('app/b.dll', b'b')
            with patch('tools.verify_windows_package.MAX_ARCHIVE_ENTRIES', 1):
                with self.assertRaisesRegex(ValueError, 'too many entries'):
                    verify_archive(archive, root)
            self.assertEqual(list(Path(temp).glob('paperbatch-package-check-*')), [])

if __name__ == '__main__': unittest.main()
