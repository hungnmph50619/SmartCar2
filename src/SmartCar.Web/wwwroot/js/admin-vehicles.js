(() => {
    const rows = Array.from(document.querySelectorAll('.vehicle-row'));
    if (!rows.length) return;

    const search = document.getElementById('vehicleSearch');
    const brand = document.getElementById('brandFilter');
    const status = document.getElementById('statusFilter');
    const transmission = document.getElementById('transmissionFilter');
    const year = document.getElementById('yearFilter');
    const clear = document.getElementById('clearVehicleFilters');
    const resultCount = document.getElementById('vehicleResultCount');
    const pageSize = document.getElementById('vehiclePageSize');
    const pagination = document.getElementById('vehiclePagination');
    const empty = document.getElementById('vehicleEmptyState');
    let currentPage = 1;

    const normalize = value => (value || '').toLocaleLowerCase('vi').trim();

    function filteredRows() {
        const q = normalize(search.value);
        return rows.filter(row => {
            const matchesSearch = !q || normalize(`${row.dataset.name} ${row.dataset.model} ${row.dataset.license}`).includes(q);
            const matchesBrand = !brand.value || row.dataset.brand === brand.value;
            const matchesStatus = !status.value || row.dataset.status === status.value;
            const matchesTransmission = !transmission.value || row.dataset.transmission === transmission.value;
            const matchesYear = !year.value || row.dataset.year === year.value;
            return matchesSearch && matchesBrand && matchesStatus && matchesTransmission && matchesYear;
        });
    }

    function renderPagination(totalPages) {
        pagination.innerHTML = '';
        if (totalPages <= 1) return;
        const addButton = (label, page, active = false, disabled = false) => {
            const button = document.createElement('button');
            button.type = 'button';
            button.className = `vehicle-page-btn${active ? ' active' : ''}`;
            button.textContent = label;
            button.disabled = disabled;
            button.addEventListener('click', () => { currentPage = page; render(); });
            pagination.appendChild(button);
        };
        addButton('‹', Math.max(1, currentPage - 1), false, currentPage === 1);
        const start = Math.max(1, currentPage - 2);
        const end = Math.min(totalPages, start + 4);
        for (let p = Math.max(1, end - 4); p <= end; p++) addButton(String(p), p, p === currentPage);
        addButton('›', Math.min(totalPages, currentPage + 1), false, currentPage === totalPages);
    }

    function render() {
        const filtered = filteredRows();
        const size = Number(pageSize.value) || 8;
        const totalPages = Math.max(1, Math.ceil(filtered.length / size));
        currentPage = Math.min(currentPage, totalPages);
        const start = (currentPage - 1) * size;
        const visible = new Set(filtered.slice(start, start + size));
        rows.forEach(row => row.hidden = !visible.has(row));
        resultCount.textContent = `${filtered.length} xe`;
        empty.hidden = filtered.length !== 0;
        renderPagination(totalPages);
    }

    function statusClass(value) {
        return `vehicle-status status-${(value || '').toLowerCase()}`;
    }

    function formatMoney(value) {
        const number = Number(value || 0);
        return `${new Intl.NumberFormat('vi-VN').format(number)} đ`;
    }

    function formatMileage(value) {
        return `${new Intl.NumberFormat('vi-VN').format(Number(value || 0))} km`;
    }

    const detailEmpty = document.getElementById('vehicleDetailEmpty');
    const detailContent = document.getElementById('vehicleDetailContent');
    const fields = id => document.getElementById(id);

    function selectVehicle(row) {
        rows.forEach(item => item.classList.toggle('is-selected', item === row));
        detailEmpty.hidden = true;
        detailContent.hidden = false;
        fields('detailStatus').className = statusClass(row.dataset.status);
        fields('detailStatus').textContent = row.dataset.statusLabel;
        fields('detailName').textContent = row.dataset.name || '—';
        fields('detailModel').textContent = row.dataset.model || '—';
        fields('detailLicense').textContent = row.dataset.license || '—';
        fields('detailBrand').textContent = row.dataset.brand || '—';
        fields('detailYear').textContent = row.dataset.year || '—';
        fields('detailColor').textContent = row.dataset.color || '—';
        fields('detailTransmission').textContent = row.dataset.transmission || '—';
        fields('detailFuel').textContent = row.dataset.fuel || '—';
        fields('detailSeats').textContent = `${row.dataset.seats || '—'} chỗ`;
        fields('detailMileage').textContent = formatMileage(row.dataset.mileage);
        fields('detailPrice').textContent = formatMoney(row.dataset.price);

        const image = fields('detailImage');
        const placeholder = fields('detailImagePlaceholder');
        if (row.dataset.image) {
            image.src = row.dataset.image;
            image.hidden = false;
            placeholder.hidden = true;
        } else {
            image.removeAttribute('src');
            image.hidden = true;
            placeholder.hidden = false;
        }

        const desc = row.dataset.description || '';
        fields('detailDescriptionWrap').hidden = !desc;
        fields('detailDescription').textContent = desc;
        setLegalState(row);
        setDocumentState('detailDocRegistration', row.dataset.docRegistration, 'Đăng ký xe');
        setDocumentState('detailDocInspection', row.dataset.docInspection, 'Đăng kiểm');
        setDocumentState('detailDocInsurance', row.dataset.docInsurance, 'Bảo hiểm');
        setDocumentState('detailDocRoadFee', row.dataset.docRoadfee, 'Phí đường bộ');
        fields('detailEditLink').href = `/AdminVehicles/Edit/${row.dataset.id}`;
        const docsUrl = `/VehicleDocuments?vehicleId=${encodeURIComponent(row.dataset.id)}`;
        fields('detailDocumentsLink').href = docsUrl;
        fields('detailDocumentAction').href = docsUrl;
    }

    rows.forEach(row => {
        row.addEventListener('click', () => selectVehicle(row));
        row.addEventListener('keydown', e => {
            if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); selectVehicle(row); }
        });
    });

    [search, brand, status, transmission, year].forEach(input => input.addEventListener(input.tagName === 'SELECT' ? 'change' : 'input', () => { currentPage = 1; render(); }));
    pageSize.addEventListener('change', () => { currentPage = 1; render(); });
    clear.addEventListener('click', () => { search.value=''; brand.value=''; status.value=''; transmission.value=''; year.value=''; currentPage=1; render(); });

    document.querySelectorAll('[data-detail-tab]').forEach(button => button.addEventListener('click', () => {
        document.querySelectorAll('[data-detail-tab]').forEach(item => item.classList.remove('active'));
        document.querySelectorAll('.vehicle-tab-pane').forEach(item => item.classList.remove('active'));
        button.classList.add('active');
        fields(`detail-${button.dataset.detailTab}`).classList.add('active');
    }));
    fields('closeVehicleDetail').addEventListener('click', () => {
        rows.forEach(item => item.classList.remove('is-selected'));
        detailContent.hidden = true;
        detailEmpty.hidden = false;
    });

    render();
    const firstVisible = filteredRows()[0];
    if (firstVisible && window.innerWidth >= 992) selectVehicle(firstVisible);

    function setLegalState(row) {
        const summary = fields('detailLegalSummary');
        const title = fields('detailLegalTitle');
        const reasonsList = fields('detailLegalReasons');
        if (!summary || !title || !reasonsList) return;

        const eligible = String(row.dataset.legalEligible).toLowerCase() === 'true';
        summary.classList.toggle('eligible', eligible);
        summary.classList.toggle('ineligible', !eligible);
        title.textContent = eligible
            ? '✓ Đủ điều kiện pháp lý cho thuê'
            : '⚠ Chưa đủ điều kiện pháp lý cho thuê';

        reasonsList.innerHTML = '';
        if (eligible) {
            const item = document.createElement('li');
            item.textContent = 'Đăng ký xe, Đăng kiểm, Bảo hiểm và Phí đường bộ đều đang hợp lệ.';
            reasonsList.appendChild(item);
            return;
        }

        let reasons = [];
        try {
            reasons = JSON.parse(row.dataset.legalReasons || '[]');
        } catch {
            reasons = [];
        }

        if (!Array.isArray(reasons) || reasons.length === 0) {
            reasons = ['Chưa xác định được nguyên nhân. Hãy mở quản lý giấy tờ để kiểm tra.'];
        }

        reasons.forEach(reason => {
            const item = document.createElement('li');
            item.textContent = reason;
            reasonsList.appendChild(item);
        });
    }

    function setDocumentState(id, value, label) {
        const element = document.getElementById(id);
        if (!element) return;
        const exists = String(value).toLowerCase() === 'true';
        element.classList.toggle('document-present', exists);
        element.classList.toggle('document-missing', !exists);
        element.textContent = `${exists ? '✓' : '○'} ${label} — ${exists ? 'Đã có' : 'Chưa có'}`;
    }
})();
