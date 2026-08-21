(() => {
    const normalize = value => (value ?? '').replace(/\s+/g, ' ').trim();

    const initializeCustomerTripRecords = () => {
        if (!/^\/Bookings\/Details(?:\/|$)/i.test(window.location.pathname)) {
            return;
        }

        const completed = Array.from(document.querySelectorAll('.badge'))
            .some(badge => normalize(badge.textContent) === 'Đã hoàn tất');
        const recordTitles = new Set(['Biên bản giao xe', 'Biên bản trả xe']);
        const recordCards = [];

        document.querySelectorAll('.card').forEach(card => {
            const header = card.querySelector(':scope > .card-header');
            const title = header?.querySelector('strong')?.textContent?.trim();
            if (!header || !title || !recordTitles.has(title)) return;
            recordCards.push(card);
        });

        if (completed && recordCards.length > 0) {
            const wrapper = document.createElement('div');
            wrapper.className = 'card shadow-sm border-success mb-4';
            wrapper.innerHTML = `
                <div class="card-body">
                    <div class="d-flex justify-content-between align-items-center gap-3 flex-wrap mb-2">
                        <div>
                            <h2 class="h5 text-success mb-1">✓ Chuyến thuê đã hoàn tất</h2>
                            <div class="text-muted small">Hồ sơ chuyến gồm biên bản giao, biên bản trả, ảnh đối chiếu và thông tin quyết toán của chuyến.</div>
                        </div>
                        <button type="button" class="btn btn-success" data-customer-trip-record-toggle>Xem hồ sơ chuyến thuê</button>
                    </div>
                    <div class="d-none mt-3" data-customer-trip-record-content></div>
                </div>`;

            const first = recordCards[0];
            first.before(wrapper);
            const content = wrapper.querySelector('[data-customer-trip-record-content]');
            recordCards.forEach(card => {
                card.classList.add('mb-3');
                content.appendChild(card);
            });

            const toggle = wrapper.querySelector('[data-customer-trip-record-toggle]');
            toggle.addEventListener('click', () => {
                const opening = content.classList.contains('d-none');
                content.classList.toggle('d-none', !opening);
                toggle.textContent = opening ? 'Ẩn hồ sơ chuyến thuê' : 'Xem hồ sơ chuyến thuê';
            });
        }

        document.querySelectorAll('.card').forEach(card => {
            const header = card.querySelector(':scope > .card-header');
            const title = header?.querySelector('strong')?.textContent?.trim();
            if (!header || !title || !recordTitles.has(title)) return;

            const body = card.querySelector(':scope > .card-body');
            if (!body || body.dataset.compactRecordReady === 'true') return;
            body.dataset.compactRecordReady = 'true';

            const firstRow = body.querySelector(':scope > .row');
            const summaryValues = firstRow ? Array.from(firstRow.querySelectorAll('.fw-semibold')).map(item => item.textContent?.trim()).filter(Boolean) : [];
            const signedLink = Array.from(header.querySelectorAll('a')).find(link => link.textContent?.trim() === 'Xem bản ký');
            const compact = document.createElement('div');
            compact.className = 'card-body py-3';
            const row = document.createElement('div');
            row.className = 'd-flex justify-content-between align-items-center gap-3 flex-wrap';
            const info = document.createElement('div');
            info.className = 'small';
            const summaryLine = document.createElement('div');
            summaryLine.className = 'fw-semibold';
            summaryLine.textContent = summaryValues.length > 0 ? summaryValues.join(' · ') : 'Đã có biên bản điện tử';
            info.appendChild(summaryLine);

            const actions = document.createElement('div');
            actions.className = 'd-flex gap-2 align-items-center flex-wrap';
            if (signedLink) {
                const badge = document.createElement('span');
                badge.className = 'badge bg-success';
                badge.textContent = 'Đã có chữ ký';
                actions.appendChild(badge);
            }
            const toggle = document.createElement('button');
            toggle.type = 'button';
            toggle.className = 'btn btn-sm btn-outline-primary';
            toggle.textContent = 'Xem chi tiết';
            toggle.setAttribute('aria-expanded', 'false');
            body.classList.add('d-none');
            toggle.addEventListener('click', () => {
                const isOpening = body.classList.contains('d-none');
                body.classList.toggle('d-none', !isOpening);
                toggle.textContent = isOpening ? 'Thu gọn' : 'Xem chi tiết';
                toggle.setAttribute('aria-expanded', isOpening ? 'true' : 'false');
            });
            actions.appendChild(toggle);
            row.append(info, actions);
            compact.appendChild(row);
            body.before(compact);
        });
    };

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initializeCustomerTripRecords);
    else initializeCustomerTripRecords();
})();
