"""D2-T03: actual upstream parser without Windows mutations."""
import importlib.util
import pathlib
import types
import unittest
from test_day2_plugin import module

ROOT=pathlib.Path('/tmp/tiny11-cbi-day2-119')


class NetworkParserTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path=ROOT/'cloudbaseinit/utils/debiface.py'
        if not path.exists():
            raise unittest.SkipTest('Pinned Cloudbase source unavailable; not PASS')
        import sys
        model_spec=importlib.util.spec_from_file_location('network_model',ROOT/'cloudbaseinit/models/network.py')
        model=importlib.util.module_from_spec(model_spec)
        model_spec.loader.exec_module(model)
        for name in ('cloudbaseinit.models','oslo_log'):
            if name not in sys.modules:
                item=types.ModuleType(name);item.__path__=[];sys.modules[name]=item
        sys.modules['cloudbaseinit.models'].network=model
        log=types.SimpleNamespace(**{k:lambda *a,**kw:None for k in ('debug','info','error','warning')})
        sys.modules['oslo_log'].log=types.SimpleNamespace(getLogger=lambda n:log)
        parser_spec=importlib.util.spec_from_file_location('real_debiface',path)
        cls.parser=importlib.util.module_from_spec(parser_spec)
        parser_spec.loader.exec_module(cls.parser)

    def service(self,text):
        return types.SimpleNamespace(get_content=lambda n:text.encode(),get_network_details=lambda:self.parser.parse(text))

    def test_valid_dhcp_retains_inbox_defaults(self):
        self.assertIsNone(module._network(self.service('auto eth0\niface eth0 inet dhcp\n')))

    def test_static_missing_address_is_failure(self):
        with self.assertRaises(ValueError):
            module._network(self.service('auto eth0\niface eth0 inet static\n netmask 255.255.252.0\n'))

    def test_valid_generated_static(self):
        text='auto eth0\niface eth0 inet static\n address 10.10.10.188\n netmask 255.255.252.0\n gateway 10.10.10.1\n dns-nameservers 10.10.10.1\n'
        self.assertEqual(module._network(self.service(text)),{'mode':'static','ip':'10.10.10.188','prefix':22,'gateway':'10.10.10.1','dns':['10.10.10.1']})

    def test_missing_network_definition_is_failure(self):
        with self.assertRaises(ValueError):
            module._network(self.service('auto lo\niface lo inet loopback\n'))
