(() => {
    const root = document.getElementById('policy-groups');
    if (!root) return;
    const conflict = root.dataset.conflict === 'true';
    const cards = Array.from(root.querySelectorAll('[data-policy-group]'));
    let submitting = false;
    const inputs = card => Array.from(card.querySelectorAll('[data-policy-input]'));
    const changed = input => input.type === 'number'
        ? input.value.trim() === '' || Number(input.value) !== Number(input.dataset.current)
        : input.value.replace(/\r\n/g, '\n') !== input.dataset.current.replace(/\r\n/g, '\n');
    const dirty = card => inputs(card).some(changed);
    const format = (value, unit) => `${new Intl.NumberFormat('vi-VN', { maximumFractionDigits: 2 }).format(Number(value))} ${unit}`;
    const render = card => {
        const changes = inputs(card).filter(changed);
        const output = card.querySelector('[data-changes]');
        output.replaceChildren();
        output.hidden = changes.length === 0;
        if (changes.length) {
            const heading = document.createElement('strong');
            heading.textContent = 'Thay đổi chưa lưu';
            output.append(heading);
            changes.forEach(input => {
                const row = document.createElement('div');
                row.className = 'small mt-1';
                row.textContent = input.type === 'number'
                    ? `${input.dataset.label}: ${format(input.dataset.current, input.dataset.unit)} → ${input.value === '' ? 'Chưa nhập' : format(input.value, input.dataset.unit)}`
                    : `${input.dataset.label}: đã chỉnh sửa nội dung.`;
                output.append(row);
            });
        }
        card.querySelector('[data-save]').disabled = conflict || changes.length === 0;
    };
    const close = card => {
        inputs(card).forEach(input => { input.value = input.dataset.current; });
        card.querySelector('[data-policy-form]').hidden = true;
        card.querySelector('[data-edit]').setAttribute('aria-expanded', 'false');
        render(card);
    };
    cards.forEach(card => {
        const form = card.querySelector('[data-policy-form]');
        card.querySelector('[data-edit]').addEventListener('click', () => {
            if (conflict || !form.hidden) return;
            const active = cards.find(other => other !== card && !other.querySelector('[data-policy-form]').hidden);
            if (active && dirty(active) && !window.confirm('Nhóm đang sửa có thay đổi chưa lưu. Bỏ thay đổi để mở nhóm khác?')) return;
            if (active) close(active);
            form.hidden = false;
            card.querySelector('[data-edit]').setAttribute('aria-expanded', 'true');
            inputs(card)[0]?.focus();
        });
        card.querySelector('[data-cancel]').addEventListener('click', () => {
            if (dirty(card) && !window.confirm('Bỏ các thay đổi chưa lưu của nhóm này?')) return;
            close(card);
            card.querySelector('[data-edit]').focus();
        });
        inputs(card).forEach(input => input.addEventListener('input', () => render(card)));
        form.addEventListener('submit', event => {
            if (conflict || submitting || !dirty(card) || !form.reportValidity()) {
                event.preventDefault();
                return;
            }
            submitting = true;
            card.querySelector('[data-save]').disabled = true;
            card.querySelector('[data-save]').textContent = 'Đang lưu…';
        });
        render(card);
    });
    window.addEventListener('beforeunload', event => {
        if (!submitting && cards.some(dirty)) {
            event.preventDefault();
            event.returnValue = '';
        }
    });
    // Restore controls when navigating back from the browser's page cache.
    window.addEventListener('pageshow', () => {
        submitting = false;
        cards.forEach(card => {
            card.querySelector('[data-save]').textContent = 'Lưu thay đổi';
            render(card);
        });
    });
})();
