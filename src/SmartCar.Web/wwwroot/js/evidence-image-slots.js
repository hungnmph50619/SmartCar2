(() => {
    document.addEventListener('DOMContentLoaded', () => {
        document.querySelectorAll('[data-evidence-slot]').forEach(initializeSlot);
        document.querySelectorAll('[data-evidence-multiple]').forEach(initializeMultiple);
    });

    function initializeSlot(slot) {
        const input = slot.querySelector('[data-evidence-input]');
        const preview = slot.querySelector('[data-evidence-preview]');
        const name = slot.querySelector('[data-evidence-name]');
        let objectUrl = null;

        if (!(input instanceof HTMLInputElement)) return;

        const render = () => {
            if (objectUrl) {
                URL.revokeObjectURL(objectUrl);
                objectUrl = null;
            }

            const file = input.files?.[0] || null;
            slot.classList.toggle('border-success', Boolean(file));
            if (name) name.textContent = file ? file.name : 'Chưa chọn ảnh';

            if (!preview) return;
            if (!file) {
                preview.removeAttribute('src');
                preview.classList.add('d-none');
                return;
            }

            objectUrl = URL.createObjectURL(file);
            preview.src = objectUrl;
            preview.classList.remove('d-none');
        };

        input.addEventListener('change', render);
        render();
    }

    function initializeMultiple(root) {
        const input = root.querySelector('[data-evidence-multiple-input]');
        const preview = root.querySelector('[data-evidence-multiple-preview]');
        const count = root.querySelector('[data-evidence-count]');
        let objectUrls = [];

        if (!(input instanceof HTMLInputElement)) return;

        const render = () => {
            objectUrls.forEach(URL.revokeObjectURL);
            objectUrls = [];
            const files = Array.from(input.files || []);
            if (count) count.textContent = files.length ? `Đã chọn ${files.length} ảnh` : 'Chưa chọn ảnh';
            if (!preview) return;
            preview.innerHTML = '';

            files.forEach((file) => {
                const url = URL.createObjectURL(file);
                objectUrls.push(url);
                const card = document.createElement('div');
                card.className = 'border rounded-3 bg-white p-1';
                card.style.width = '112px';

                const image = document.createElement('img');
                image.src = url;
                image.alt = file.name;
                image.className = 'rounded-2 border bg-light w-100';
                image.style.height = '78px';
                image.style.objectFit = 'cover';

                const caption = document.createElement('div');
                caption.className = 'small text-muted text-truncate mt-1';
                caption.title = file.name;
                caption.textContent = file.name;
                card.append(image, caption);
                preview.appendChild(card);
            });
        };

        input.addEventListener('change', render);
        render();
    }
})();
