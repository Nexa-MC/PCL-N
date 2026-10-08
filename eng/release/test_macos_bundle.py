import plistlib
import unittest

from macos_bundle import bundle_info


class MacOSBundleTests(unittest.TestCase):
    def test_signed_bundle_declares_only_confirmed_local_pack_formats(self):
        info = plistlib.loads(plistlib.dumps(bundle_info("2.0.0.alpha.6", "2.0.0")))
        self.assertEqual("org.nexacl.launcher", info["CFBundleIdentifier"])
        self.assertEqual("2.0.0.alpha.6", info["NexaProductVersion"])
        self.assertEqual(["nexacl"], info["CFBundleURLTypes"][0]["CFBundleURLSchemes"])
        self.assertEqual({"mrpack", "nexapack"}, {
            extension for entry in info["CFBundleDocumentTypes"] for extension in entry["CFBundleTypeExtensions"]})
        self.assertTrue(all(entry["CFBundleTypeRole"] == "Viewer" and entry["LSHandlerRank"] == "Alternate"
                            for entry in info["CFBundleDocumentTypes"]))
        declarations = info["UTImportedTypeDeclarations"]
        self.assertEqual({"org.modrinth.mrpack", "org.nexacl.nexapack"}, {entry["UTTypeIdentifier"] for entry in declarations})
        self.assertNotIn("zip", [value for entry in declarations for value in entry["UTTypeTagSpecification"]["public.filename-extension"]])


if __name__ == "__main__":
    unittest.main()
