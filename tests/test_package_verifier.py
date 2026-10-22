import hashlib
import json
import tempfile
import unittest
import zipfile
from pathlib import Path

from tools.verify_windows_package import validate, verify_archive

class PackageVerifierTests(unittest.TestCase):
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

    def test_rooted_manifest_path_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); self.fixture(root)
            m = json.loads((root / 'package-manifest.json').read_text()); m['files'][0]['path'] = '/outside'
            (root / 'package-manifest.json').write_text(json.dumps(m))
            with self.assertRaisesRegex(ValueError, 'unsafe package path'):
                validate(root)

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
            with zipfile.ZipFile(archive, 'w') as z:
                for p in root.rglob('*'):
                    if p.is_file(): z.write(p, p.relative_to(root).as_posix())
                z.writestr('LICENSE', b'overwrite')
            with self.assertRaisesRegex(ValueError, 'duplicate ZIP entry'):
                verify_archive(archive, root)

if __name__ == '__main__': unittest.main()
