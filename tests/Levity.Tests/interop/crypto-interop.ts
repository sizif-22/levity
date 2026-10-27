// Run by CryptoInteropTests. Reads {"keyHex","hostFrame","phoneMessage"} on stdin and uses the phone app's
// real crypto module to (1) open a frame the C# host produced and (2) seal a frame for the C# host to open.
import { SecureChannel, importKey, roomIdFromKey } from '../../../web/src/lib/crypto.ts';

let text = '';
for await (const chunk of process.stdin) text += chunk;
const input = JSON.parse(text);

const raw = Uint8Array.from(input.keyHex.match(/../g).map((h: string) => parseInt(h, 16)));
const channel = new SecureChannel(await importKey(raw));

console.log(JSON.stringify({
  roomId: await roomIdFromKey(raw),
  opened: await channel.open(input.hostFrame),
  phoneFrame: await channel.seal(input.phoneMessage),
}));
