/** Device credentials live in IndexedDB so the AES key can be stored as a non-extractable CryptoKey. */

export interface DeviceRecord {
  deviceId: string;
  name: string;
  relay: string;
  roomId: string;
  key: CryptoKey;
}

const DB = 'levity';
const STORE = 'device';
const ID = 'current';

function open(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB, 1);
    req.onupgradeneeded = () => req.result.createObjectStore(STORE);
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

async function run<T>(mode: IDBTransactionMode, fn: (s: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  const db = await open();
  return new Promise<T>((resolve, reject) => {
    const req = fn(db.transaction(STORE, mode).objectStore(STORE));
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

export const loadDevice = () => run<DeviceRecord | undefined>('readonly', (s) => s.get(ID));
export const saveDevice = (d: DeviceRecord) => run('readwrite', (s) => s.put(d, ID));
export const clearDevice = () => run('readwrite', (s) => s.delete(ID));
