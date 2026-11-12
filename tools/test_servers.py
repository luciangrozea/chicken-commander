"""Local FTP (127.0.0.1:2121) and SFTP (127.0.0.1:2222) servers for manually testing Chicken Commander.
Test-only credentials: user 'demo', password 'demo'. Both serve the folder given as argv[1]. Localhost only."""
import asyncio
import os
import sys
import threading

import asyncssh
from pyftpdlib.authorizers import DummyAuthorizer
from pyftpdlib.handlers import FTPHandler
from pyftpdlib.servers import FTPServer

ROOT = os.path.abspath(sys.argv[1])
KEY = os.path.join(os.path.dirname(os.path.abspath(__file__)), "test_host_key")


def run_ftp():
    auth = DummyAuthorizer()
    auth.add_user("demo", "demo", ROOT, perm="elradfmwMT")
    handler = FTPHandler
    handler.authorizer = auth
    handler.passive_ports = range(60000, 60100)
    FTPServer(("127.0.0.1", 2121), handler).serve_forever()


class DemoSSH(asyncssh.SSHServer):
    def password_auth_supported(self):
        return True

    def validate_password(self, username, password):
        return username == "demo" and password == "demo"


async def run_sftp():
    if not os.path.exists(KEY):
        asyncssh.generate_private_key("ssh-ed25519").write_private_key(KEY)
    await asyncssh.listen(
        "127.0.0.1", 2222, server_host_keys=[KEY], server_factory=DemoSSH,
        sftp_factory=lambda chan: asyncssh.SFTPServer(chan, chroot=ROOT.encode()),
    )
    print("FTP on 2121, SFTP on 2222, serving", ROOT, flush=True)
    await asyncio.Future()


threading.Thread(target=run_ftp, daemon=True).start()
asyncio.run(run_sftp())
