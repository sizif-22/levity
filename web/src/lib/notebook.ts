export interface NoteImage {
  id: string;
  name: string;
  mimeType: string;
  url: string;
  data?: string;
  size?: number;
}

export interface NoteItem {
  id: string;
  title: string;
  content: string;
  images?: NoteImage[];
  createdAt: number;
  updatedAt: number;
  pinned?: boolean;
}

export interface NotebookDataMsg {
  type: 'notebook_data';
  notes: NoteItem[];
}

const DB_NAME = 'levity';
const STORE_NAME = 'notebook_cache';
const KEY = 'notes';

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME);
    req.onupgradeneeded = () => {
      const db = req.result;
      if (!db.objectStoreNames.contains(STORE_NAME)) {
        db.createObjectStore(STORE_NAME);
      }
    };
    req.onsuccess = () => {
      const db = req.result;
      if (!db.objectStoreNames.contains(STORE_NAME)) {
        // Upgrade needed for new store
        const version = db.version + 1;
        db.close();
        const upgradeReq = indexedDB.open(DB_NAME, version);
        upgradeReq.onupgradeneeded = () => {
          upgradeReq.result.createObjectStore(STORE_NAME);
        };
        upgradeReq.onsuccess = () => resolve(upgradeReq.result);
        upgradeReq.onerror = () => reject(upgradeReq.error);
        return;
      }
      resolve(db);
    };
    req.onerror = () => reject(req.error);
  });
}

export async function loadCachedNotes(): Promise<NoteItem[]> {
  try {
    const db = await openDb();
    return new Promise((resolve) => {
      const tx = db.transaction(STORE_NAME, 'readonly');
      const store = tx.objectStore(STORE_NAME);
      const req = store.get(KEY);
      req.onsuccess = () => resolve((req.result as NoteItem[]) || []);
      req.onerror = () => resolve([]);
    });
  } catch {
    return [];
  }
}

export async function saveCachedNotes(notes: NoteItem[]): Promise<void> {
  try {
    const db = await openDb();
    await new Promise<void>((resolve, reject) => {
      const tx = db.transaction(STORE_NAME, 'readwrite');
      const store = tx.objectStore(STORE_NAME);
      const req = store.put(notes, KEY);
      req.onsuccess = () => resolve();
      req.onerror = () => reject(req.error);
    });
  } catch {
    // ignore IDB failure
  }
}
