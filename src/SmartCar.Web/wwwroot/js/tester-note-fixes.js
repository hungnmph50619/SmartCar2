document.addEventListener('DOMContentLoaded', () => {
    const timeInputIds = ['handover-time', 'return-time', 'requested-return-time', 'admin-requested-return-time'];

    timeInputIds.forEach(id => {
        const input = document.getElementById(id);
        if (!(input instanceof HTMLInputElement)) return;
        const currentValue = input.value;
        input.type = 'time';
        input.step = '60';
        input.removeAttribute('inputmode');
        input.removeAttribute('maxlength');
        input.removeAttribute('pattern');
        input.placeholder = '';
        if (currentValue) input.value = currentValue;
    });

    const fuelInputs = [
        document.getElementById('handover-fuel'),
        document.querySelector('form[action*="Returns"] input[name="FuelLevel"]')
    ].filter(input => input instanceof HTMLInputElement);

    fuelInputs.forEach(input => {
        input.type = 'number';
        input.min = '0';
        input.max = '100';
        input.step = '1';
        input.inputMode = 'numeric';

        const normalize = () => {
            const raw = String(input.value || '').replace(/[^0-9]/g, '').slice(0, 3);
            if (!raw) {
                input.value = '';
                input.setCustomValidity('');
                return;
            }

            const value = Math.min(100, Math.max(0, Number(raw)));
            input.value = String(value);
            input.setCustomValidity('');
        };

        input.addEventListener('input', normalize);
        input.addEventListener('change', normalize);
        input.addEventListener('paste', () => window.setTimeout(normalize, 0));
    });

    polishSignedDocumentUi();
});

function polishSignedDocumentUi() {
    const path = window.location.pathname;
    const isCustomerDetails = /^\/Bookings\/Details(?:\/|$)/i.test(path);
    const isAdminInspection = /^\/Returns\/Inspect(?:\/|$)/i.test(path);

    if (!isCustomerDetails && !isAdminInspection) {
        return;
    }

    document.querySelectorAll('.card').forEach(card => {
        const header = card.querySelector(':scope > .card-header');
        const body = card.querySelector(':scope > .card-body');
        const title = header?.querySelector('strong')?.textContent?.trim();

        if (!header || !body || !title || !['Biên bản giao xe', 'Biên bản trả xe'].includes(title)) {
            return;
        }

        const signedLinks = Array.from(body.querySelectorAll('a[href*="signed-handover-"], a[href*="signed-return-"]'));
        const signedCount = signedLinks.length;
        const badge = header.querySelector('.badge');

        if (badge) {
            badge.className = signedCount > 0 ? 'badge bg-success' : 'badge bg-secondary';
            badge.textContent = signedCount > 0 ? `Đã ký ${signedCount} trang` : 'Chưa ký';
        }

        signedLinks.forEach((link, index) => {
            link.className = 'btn btn-sm btn-outline-success text-decoration-none';
            link.textContent = `Trang ${index + 1}`;
        });

        body.querySelectorAll('strong').forEach(strong => {
            const text = strong.textContent?.trim() ?? '';
            if (text === 'Bản ký biên bản giao:' || text === 'Bản ký biên bản trả:' || text.startsWith('Bản ký ')) {
                strong.textContent = signedCount > 0 ? `Bản ký ${signedCount} trang:` : 'Bản ký:';
            }
        });

        body.querySelectorAll('.col-6.col-md-4').forEach(column => {
            if (!column.querySelector('img')) return;
            column.querySelectorAll(':scope > .small.fw-semibold.mb-1').forEach(label => label.remove());
        });
    });

    if (isAdminInspection) {
        document.querySelectorAll('a.btn, button.btn').forEach(button => {
            const text = button.textContent?.trim();
            if (text === 'In') {
                button.textContent = 'In biên bản';
            }
            if (text === 'Sửa') {
                button.textContent = 'Sửa biên bản';
            }
        });
    }
}
