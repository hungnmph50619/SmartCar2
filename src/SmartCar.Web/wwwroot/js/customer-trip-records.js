(() => {
    const normalize = value => (value ?? '').replace(/\s+/g, ' ').trim();
    const parseMoney = value => {
        const digits = (value ?? '').replace(/[^0-9-]/g, '');
        const amount = Number.parseInt(digits || '0', 10);
        return Number.isFinite(amount) ? amount : 0;
    };
    const money = value => new Intl.NumberFormat('vi-VN').format(Math.max(0, value)) + ' đ';

    const cardTitle = card => normalize(card?.querySelector(':scope > .card-header strong')?.textContent);

    const initializeCustomerTripRecords = () => {
        if (!/^\/Bookings\/Details(?:\/|$)/i.test(window.location.pathname)) return;

        const completed = Array.from(document.querySelectorAll('.badge'))
            .some(badge => normalize(badge.textContent) === 'Đã hoàn tất');
        if (!completed) return;

        const recordTitles = new Set(['Biên bản giao xe', 'Biên bản trả xe']);
        const allCards = Array.from(document.querySelectorAll('.card'));
        const recordCards = allCards.filter(card => recordTitles.has(cardTitle(card)));
        const paymentCard = allCards.find(card => cardTitle(card) === 'Thanh toán');
        const refundCard = allCards.find(card => cardTitle(card) === 'Hoàn tiền');

        // Gom biên bản giao/trả thành một hồ sơ chuyến, thay vì để hai card rời rạc.
        if (recordCards.length > 0) {
            const wrapper = document.createElement('div');
            wrapper.className = 'card shadow-sm border-success mb-4';
            wrapper.innerHTML = `
                <div class="card-body">
                    <div class="d-flex justify-content-between align-items-center gap-3 flex-wrap">
                        <div>
                            <h2 class="h5 text-success mb-1">✓ Chuyến thuê đã hoàn tất</h2>
                            <div class="text-muted small">Biên bản giao, biên bản trả, ảnh đối chiếu và bản ký được lưu chung trong hồ sơ chuyến.</div>
                        </div>
                        <button type="button" class="btn btn-success" data-customer-trip-record-toggle>Xem hồ sơ chuyến thuê</button>
                    </div>
                    <div class="d-none mt-3" data-customer-trip-record-content></div>
                </div>`;

            recordCards[0].before(wrapper);
            const content = wrapper.querySelector('[data-customer-trip-record-content]');
            recordCards.forEach(card => {
                card.classList.remove('mb-4');
                card.classList.add('mb-3');
                content?.appendChild(card);
            });

            const toggle = wrapper.querySelector('[data-customer-trip-record-toggle]');
            toggle?.addEventListener('click', () => {
                const opening = content?.classList.contains('d-none') === true;
                content?.classList.toggle('d-none', !opening);
                toggle.textContent = opening ? 'Ẩn hồ sơ chuyến thuê' : 'Xem hồ sơ chuyến thuê';
            });
        }

        // Thu gọn từng biên bản bên trong hồ sơ để khách chỉ mở khi cần xem chi tiết.
        recordCards.forEach(card => {
            const body = card.querySelector(':scope > .card-body');
            if (!body || body.dataset.compactRecordReady === 'true') return;
            body.dataset.compactRecordReady = 'true';

            const firstRow = body.querySelector(':scope > .row');
            const summaryValues = firstRow
                ? Array.from(firstRow.querySelectorAll('.fw-semibold')).map(item => normalize(item.textContent)).filter(Boolean)
                : [];
            const signedLink = Array.from(card.querySelectorAll(':scope > .card-header a'))
                .find(link => normalize(link.textContent) === 'Xem bản ký');

            const compact = document.createElement('div');
            compact.className = 'card-body py-3';
            compact.innerHTML = `
                <div class="d-flex justify-content-between align-items-center gap-3 flex-wrap">
                    <div class="small fw-semibold">${summaryValues.length > 0 ? summaryValues.join(' · ') : 'Đã có biên bản điện tử'}</div>
                    <div class="d-flex gap-2 align-items-center flex-wrap" data-record-actions></div>
                </div>`;
            const actions = compact.querySelector('[data-record-actions]');

            if (signedLink) {
                const badge = document.createElement('span');
                badge.className = 'badge bg-success';
                badge.textContent = 'Đã có chữ ký';
                actions?.appendChild(badge);
            }

            const toggle = document.createElement('button');
            toggle.type = 'button';
            toggle.className = 'btn btn-sm btn-outline-primary';
            toggle.textContent = 'Xem chi tiết';
            body.classList.add('d-none');
            toggle.addEventListener('click', () => {
                const opening = body.classList.contains('d-none');
                body.classList.toggle('d-none', !opening);
                toggle.textContent = opening ? 'Thu gọn' : 'Xem chi tiết';
            });
            actions?.appendChild(toggle);
            body.before(compact);
        });

        // Đổi nhãn các khoản hoàn cọc để khách hiểu nguồn hoàn tiền.
        if (refundCard) {
            const refundRows = Array.from(refundCard.querySelectorAll('.card-body > .d-flex'));
            const depositRows = refundRows.filter(row => normalize(row.querySelector('strong')?.textContent) === 'Hoàn cọc');
            depositRows.forEach((row, index) => {
                const label = row.querySelector('strong');
                if (!label) return;
                label.textContent = index === depositRows.length - 1
                    ? 'Hoàn cọc sau khi kết thúc chuyến'
                    : 'Hoàn phần cọc điều chỉnh';
            });

            const heading = refundCard.querySelector(':scope > .card-header strong');
            if (heading) heading.textContent = 'Chi tiết hoàn tiền';
        }

        // Lấy tổng đã thanh toán từ hóa đơn - đây là dòng tiền khách đã thực chuyển trong cả chuyến.
        const paidRow = Array.from(document.querySelectorAll('tfoot tr'))
            .find(row => normalize(row.textContent).includes('Đã xác nhận thanh toán'));
        const paidTotal = parseMoney(paidRow?.querySelector('td:last-child')?.textContent);

        // Chỉ cộng các khoản đã hoàn thực tế, không cộng khoản còn đang chờ hoàn.
        let refundedTotal = 0;
        if (refundCard) {
            const refundRows = Array.from(refundCard.querySelectorAll('.card-body > .d-flex'));
            refundRows.forEach(row => {
                const status = normalize(row.querySelector('.small.text-muted')?.textContent);
                if (!status.includes('Đã hoàn')) return;
                const amountCandidates = Array.from(row.querySelectorAll('div'))
                    .map(element => normalize(element.textContent))
                    .filter(text => /\d[\d.,]*\s*đ/.test(text));
                if (amountCandidates.length > 0) refundedTotal += parseMoney(amountCandidates[0]);
            });
        }

        const netPaid = Math.max(0, paidTotal - refundedTotal);

        // Card quyết toán cuối chuyến: hiển thị kết quả trước, lịch sử giao dịch xem sau.
        if (paymentCard) {
            const settlement = document.createElement('div');
            settlement.className = 'card shadow-sm border-success mb-4';
            settlement.innerHTML = `
                <div class="card-header bg-white d-flex justify-content-between align-items-center gap-2 flex-wrap">
                    <div>
                        <strong>Quyết toán chuyến thuê</strong>
                        <div class="small text-muted">Kết quả tài chính cuối cùng sau tất cả khoản thu và hoàn tiền.</div>
                    </div>
                    <span class="badge bg-success">Đã quyết toán</span>
                </div>
                <div class="card-body">
                    <div class="d-flex justify-content-between py-2 border-bottom"><span>Tổng đã thanh toán</span><strong>${money(paidTotal)}</strong></div>
                    <div class="d-flex justify-content-between py-2 border-bottom"><span>Tổng đã hoàn lại</span><strong>${money(refundedTotal)}</strong></div>
                    <div class="d-flex justify-content-between align-items-center py-3"><strong>Chi phí thực tế chuyến thuê</strong><strong class="text-primary fs-4">${money(netPaid)}</strong></div>
                    <div class="small text-muted mb-3">Chi phí thực tế = tổng tiền khách đã thanh toán − tổng tiền SmartCar đã hoàn.</div>
                    <button type="button" class="btn btn-outline-primary w-100" data-customer-payment-history-toggle>Xem chi tiết giao dịch</button>
                </div>`;
            paymentCard.before(settlement);

            paymentCard.classList.add('d-none');
            const paymentHeading = paymentCard.querySelector(':scope > .card-header strong');
            if (paymentHeading) paymentHeading.textContent = 'Lịch sử thanh toán';
            refundCard?.classList.add('d-none');

            const historyToggle = settlement.querySelector('[data-customer-payment-history-toggle]');
            historyToggle?.addEventListener('click', () => {
                const opening = paymentCard.classList.contains('d-none');
                paymentCard.classList.toggle('d-none', !opening);
                refundCard?.classList.toggle('d-none', !opening);
                historyToggle.textContent = opening ? 'Ẩn chi tiết giao dịch' : 'Xem chi tiết giao dịch';
            });
        }
    };

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initializeCustomerTripRecords);
    else initializeCustomerTripRecords();
})();
