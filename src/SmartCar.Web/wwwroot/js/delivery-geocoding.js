(() => {
    // ArcGIS là provider chính vì dự án đã dùng hạ tầng Esri cho map fallback.
    // Nominatim chỉ là fallback, sau cùng mới gọi backend SmartCar.
    const arcGisBase =
        'https://geocode.arcgis.com/arcgis/rest/services/World/GeocodeServer';

    const fetchJson = async url => {
        const response = await fetch(url, {
            headers: { 'Accept': 'application/json' }
        });

        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }

        return response.json();
    };

    const validFocus = focus => {
        const lat = Number(focus?.lat);
        const lon = Number(focus?.lon);

        return Number.isFinite(lat) && lat >= -90 && lat <= 90 &&
            Number.isFinite(lon) && lon >= -180 && lon <= 180
            ? { lat, lon }
            : null;
    };

    const normalizeArcGisCandidates = data => {
        const candidates = Array.isArray(data?.candidates) ? data.candidates : [];

        return candidates
            .map(candidate => {
                const lat = Number(candidate?.location?.y);
                const lon = Number(candidate?.location?.x);
                const displayName =
                    candidate?.address ||
                    candidate?.attributes?.Match_addr ||
                    candidate?.attributes?.LongLabel;

                return {
                    lat: String(lat),
                    lon: String(lon),
                    display_name: displayName
                };
            })
            .filter(item =>
                Number.isFinite(Number(item.lat)) &&
                Number.isFinite(Number(item.lon)) &&
                typeof item.display_name === 'string' &&
                item.display_name.trim());
    };

    const arcGisSearch = async (query, focus) => {
        const point = validFocus(focus);
        let url =
            `${arcGisBase}/findAddressCandidates` +
            `?f=json&forStorage=false&outSR=4326&countryCode=VNM&maxLocations=8` +
            `&outFields=${encodeURIComponent('Match_addr,LongLabel')}` +
            `&SingleLine=${encodeURIComponent(query)}`;

        if (point) {
            url +=
                `&location=${encodeURIComponent(point.lon + ',' + point.lat)}` +
                '&distance=50000';
        }

        const data = await fetchJson(url);
        if (data?.error) {
            throw new Error(data.error.message || 'ArcGIS geocoding error');
        }

        return normalizeArcGisCandidates(data);
    };

    const arcGisReverse = async (lat, lon) => {
        const url =
            `${arcGisBase}/reverseGeocode` +
            `?f=json&outSR=4326&langCode=vi` +
            `&location=${encodeURIComponent(lon + ',' + lat)}`;

        const data = await fetchJson(url);
        if (data?.error) {
            throw new Error(data.error.message || 'ArcGIS reverse geocoding error');
        }

        const displayName =
            data?.address?.LongLabel ||
            data?.address?.Match_addr ||
            data?.address?.Address;

        if (!displayName) return null;

        const resultLat = Number(data?.location?.y ?? lat);
        const resultLon = Number(data?.location?.x ?? lon);

        return {
            lat: String(resultLat),
            lon: String(resultLon),
            display_name: displayName
        };
    };

    const nominatimSearch = async query => {
        const url =
            'https://nominatim.openstreetmap.org/search' +
            '?format=jsonv2&limit=8&countrycodes=vn&q=' +
            encodeURIComponent(query);

        const data = await fetchJson(url);
        return Array.isArray(data) ? data : [];
    };

    const nominatimReverse = async (lat, lon) => {
        const url =
            'https://nominatim.openstreetmap.org/reverse' +
            '?format=jsonv2&addressdetails=1&zoom=18&accept-language=vi' +
            '&lat=' + encodeURIComponent(lat) +
            '&lon=' + encodeURIComponent(lon);

        const data = await fetchJson(url);
        return data?.display_name ? data : null;
    };

    const serverJson = async endpoint => {
        const response = await fetch(`/api/geocoding/${endpoint}`, {
            headers: { 'Accept': 'application/json' }
        });
        const result = await response.json().catch(() => null);

        if (!response.ok) {
            throw new Error(
                result?.message ||
                'Không thể kết nối dịch vụ địa chỉ.');
        }

        return result;
    };

    const search = async (query, focus) => {
        try {
            const results = await arcGisSearch(query, focus);
            if (results.length) return results;
        } catch (error) {
            console.warn('ArcGIS search không phản hồi, thử Nominatim:', error);
        }

        try {
            const results = await nominatimSearch(query);
            if (results.length) return results;
        } catch (error) {
            console.warn('Nominatim search không phản hồi, thử SmartCar server:', error);
        }

        const point = validFocus(focus);
        const focusQuery = point
            ? `&nearLat=${encodeURIComponent(point.lat)}&nearLon=${encodeURIComponent(point.lon)}`
            : '';

        return serverJson(
            `search?q=${encodeURIComponent(query)}${focusQuery}`);
    };

    const reverse = async (lat, lon) => {
        try {
            const result = await arcGisReverse(lat, lon);
            if (result?.display_name) return result;
        } catch (error) {
            console.warn('ArcGIS reverse không phản hồi, thử Nominatim:', error);
        }

        try {
            const result = await nominatimReverse(lat, lon);
            if (result?.display_name) return result;
        } catch (error) {
            console.warn('Nominatim reverse không phản hồi, thử SmartCar server:', error);
        }

        return serverJson(
            `reverse?lat=${encodeURIComponent(lat)}&lon=${encodeURIComponent(lon)}`);
    };

    window.SmartCarDeliveryGeocoding = { search, reverse };
})();
