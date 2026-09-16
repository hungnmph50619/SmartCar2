(() => {
    const draftDbName = 'smartcar-form-drafts';
    const draftStoreName = 'files';
    const draftMaxAgeMs = 60 * 60 * 1000;

    document.addEventListener('DOMContentLoaded', () => {
        initializeBoundedNumericInputs();
        initializeFileDrafts().catch(() => {
            // IndexedDB may be disabled by browser/private mode. Forms still work normally.
        });
    });

    function initializeBoundedNumericInputs() {
        document.querySelectorAll('input[type="number"]').forEach((input) => {
            if (!(input instanceof HTMLInputElement)) return;

            const min = parseFinite(input.min);
            const max = parseFinite(input.max);
            const integerOnly = input.dataset.integer === 'true' || input.step === '1';

            input.addEventListener('keydown', (event) => {
                if (event.key === 'e' || event.key === 'E' || event.key === '+') {
                    event.preventDefault();
                    return;
                }
                if (min !== null && min >= 0 && event.key === '-') {
                    event.preventDefault();
                    return;
                }
                if (integerOnly && (event.key === '.' || event.key === ',')) {
                    event.preventDefault();
                }
            });

            const applyHardBounds = (finalizeMinimum) => {
                if (!input.value) return;

                const value = Number(input.value);
                if (!Number.isFinite(value)) {
                    input.value = '';
                    return;
                }

                let bounded = value;
                if (max !== null && bounded > max) bounded = max;
                if (finalizeMinimum && min !== null && bounded < min) bounded = min;
                if (integerOnly) bounded = Math.trunc(bounded);

                if (bounded !== value) {
                    input.value = String(bounded);
                    input.dispatchEvent(new Event('change', { bubbles: true }));
                }
            };

            input.addEventListener('input', () => applyHardBounds(false));
            input.addEventListener('paste', () => setTimeout(() => applyHardBounds(false), 0));
            input.addEventListener('blur', () => applyHardBounds(true));
        });
    }

    function parseFinite(value) {
        if (value === '') return null;
        const number = Number(value);
        return Number.isFinite(number) ? number : null;
    }

    async function initializeFileDrafts() {
        const inputs = Array.from(document.querySelectorAll('input[type="file"][data-file-draft-key]'))
            .filter((input) => input instanceof HTMLInputElement);
        if (inputs.length === 0 || !('indexedDB' in window)) return;

        const db = await openDraftDb();
        await purgeExpiredDrafts(db);

        for (const input of inputs) {
            input.addEventListener('change', () => saveInputDraft(db, input));
            await restoreInputDraft(db, input);
        }
    }

    function openDraftDb() {
        return new Promise((resolve, reject) => {
            const request = indexedDB.open(draftDbName, 1);
            request.onupgradeneeded = () => {
                const db = request.result;
                if (!db.objectStoreNames.contains(draftStoreName)) {
                    db.createObjectStore(draftStoreName, { keyPath: 'key' });
                }
            };
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
        });
    }

    function transactionRequest(db, mode, action) {
        return new Promise((resolve, reject) => {
            const transaction = db.transaction(draftStoreName, mode);
            const store = transaction.objectStore(draftStoreName);
            const request = action(store);
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
        });
    }

    async function saveInputDraft(db, input) {
        const key = input.dataset.fileDraftKey;
        if (!key) return;

        const files = Array.from(input.files || []);
        if (files.length === 0) {
            await transactionRequest(db, 'readwrite', (store) => store.delete(key));
            return;
        }

        const payload = {
            key,
            storedAt: Date.now(),
            files: files.map((file) => ({
                name: file.name,
                type: file.type,
                lastModified: file.lastModified,
                blob: file
            }))
        };
        await transactionRequest(db, 'readwrite', (store) => store.put(payload));
    }

    async function restoreInputDraft(db, input) {
        const key = input.dataset.fileDraftKey;
        if (!key || (input.files && input.files.length > 0)) return;

        const payload = await transactionRequest(db, 'readonly', (store) => store.get(key));
        if (!payload || Date.now() - payload.storedAt > draftMaxAgeMs) return;

        const transfer = new DataTransfer();
        (payload.files || []).forEach((item) => {
            transfer.items.add(new File(
                [item.blob],
                item.name,
                { type: item.type, lastModified: item.lastModified }
            ));
        });

        input.files = transfer.files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
    }

    async function purgeExpiredDrafts(db) {
        const all = await transactionRequest(db, 'readonly', (store) => store.getAll());
        const expired = (all || []).filter((item) => Date.now() - item.storedAt > draftMaxAgeMs);
        for (const item of expired) {
            await transactionRequest(db, 'readwrite', (store) => store.delete(item.key));
        }
    }
})();
