"""D2-T04: Windows bundled Python with isolated state; never provision the host."""
import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import tempfile

plugin = pathlib.Path(sys.argv[1])
spec = importlib.util.spec_from_file_location('tiny11_cloudbase', plugin)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
with tempfile.TemporaryDirectory(prefix='tiny11-day2-state-') as temporary:
    store = module.PrivateState()
    store.root = pathlib.Path(temporary) / 'private'
    store.path = store.root / 'state.json'
    with store.locked():
        initial = store.fingerprint('synthetic-only')
        store.save({'schema':1,'applied':{},'attempts':{}})
        assert store.load()['schema'] == 1
        assert store.fingerprint('synthetic-only') == initial
    child = r'''
import importlib.util,pathlib,sys
s=importlib.util.spec_from_file_location('p',sys.argv[1]);m=importlib.util.module_from_spec(s);s.loader.exec_module(m)
t=m.PrivateState();t.root=pathlib.Path(sys.argv[2]);t.path=t.root/'state.json'
with t.locked(): print(t.fingerprint('synthetic-only'))
'''
    observed = subprocess.check_output([sys.executable,'-c',child,str(plugin),str(store.root)],text=True).strip()
    assert observed == initial
    saved = store.path.read_text()
    assert 'synthetic-only' not in saved
    assert (store.root/'key.dpapi').read_bytes() != initial.encode()
    store.path.write_text('{broken',encoding='utf-8')
    try:
        store.load()
    except json.JSONDecodeError:
        pass
    else:
        raise AssertionError('Corrupt state accepted')
    import win32security
    descriptor=win32security.GetFileSecurity(str(store.root),win32security.DACL_SECURITY_INFORMATION)
    acl=descriptor.GetSecurityDescriptorDacl()
    allowed={'S-1-5-18','S-1-5-32-544'}
    for index in range(acl.GetAceCount()):
        ace=acl.GetAce(index)
        assert win32security.ConvertSidToStringSid(ace[-1]) in allowed
    store.path.write_text(saved,encoding='utf-8')
    # A writer killed before replace must preserve committed state.
    killed = subprocess.run([sys.executable,'-c',"import os,pathlib,sys;p=pathlib.Path(sys.argv[1]);f=open(p/'.pending-killed','wb');f.write(b'broken');f.flush();os.fsync(f.fileno());os._exit(9)",str(store.root)])
    assert killed.returncode == 9 and store.path.read_text()==saved
    # Check reparse files using an administrator-created junction, without symlink privileges.
    junction=pathlib.Path(temporary)/'junction'
    subprocess.run(['cmd.exe','/c','mklink','/J',str(junction),str(store.root)],check=True,stdout=subprocess.DEVNULL)
    try:
        try: module.PrivateState._regular(junction)
        except ValueError: pass
        else: raise AssertionError('Junction accepted')
    finally: os.rmdir(junction)
    # A missing key with existing state must not generate a replacement key.
    keypath=store.root/'key.dpapi'; original=keypath.read_bytes();keypath.unlink()
    try:
        try: store.fingerprint('synthetic-only')
        except ValueError: pass
        else: raise AssertionError('Missing key accepted')
    finally:keypath.write_bytes(original)
    print(json.dumps({'DPAPIRoundTrip':True,'SeparateProcessHMAC':True,'AtomicStateRead':True,
                      'CorruptStateRejected':True,'ACLOnlySystemAdministrators':True,
                      'KilledWriterPreservesState':True,'ReparseRejected':True,'MissingKeyRejected':True,
                      'Python':sys.version.split()[0]}))
