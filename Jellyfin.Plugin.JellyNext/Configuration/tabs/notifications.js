// Notifications tab initialization and logic

function initNotificationsTab() {
    document.getElementById('SendTestEmailBtn').addEventListener('click', sendTestEmail);
    console.log('Notifications tab initialized');
}

function loadNotificationsSettings(config) {
    document.getElementById('EmailNotificationsEnabled').checked = config.EmailNotificationsEnabled === true;
    document.getElementById('NewSeasonNotificationWindowDays').value = config.NewSeasonNotificationWindowDays || 30;
    document.getElementById('SmtpHost').value = config.SmtpHost || '';
    document.getElementById('SmtpPort').value = config.SmtpPort || 587;
    document.getElementById('SmtpUseStartTls').checked = config.SmtpUseStartTls !== false;
    document.getElementById('SmtpUsername').value = config.SmtpUsername || '';
    document.getElementById('SmtpPassword').value = config.SmtpPassword || '';
    document.getElementById('SmtpFromAddress').value = config.SmtpFromAddress || '';
    document.getElementById('SmtpFromName').value = config.SmtpFromName || 'JellyNext';
}

function saveNotificationsSettings(config) {
    config.EmailNotificationsEnabled = document.getElementById('EmailNotificationsEnabled').checked;
    config.NewSeasonNotificationWindowDays =
        parseInt(document.getElementById('NewSeasonNotificationWindowDays').value, 10) || 30;
    config.SmtpHost = document.getElementById('SmtpHost').value.trim();
    config.SmtpPort = parseInt(document.getElementById('SmtpPort').value, 10) || 587;
    config.SmtpUseStartTls = document.getElementById('SmtpUseStartTls').checked;
    config.SmtpUsername = document.getElementById('SmtpUsername').value.trim();
    config.SmtpPassword = document.getElementById('SmtpPassword').value;
    config.SmtpFromAddress = document.getElementById('SmtpFromAddress').value.trim();
    config.SmtpFromName = document.getElementById('SmtpFromName').value.trim();
}

// Uses the saved configuration, not the values currently in the form - the server sends the mail.
function sendTestEmail() {
    var recipient = document.getElementById('TestEmailRecipient').value.trim();
    if (!recipient) {
        Dashboard.alert('Enter an address to send the test message to.');
        return;
    }

    Dashboard.showLoadingMsg();

    ApiClient.fetch({
        type: 'POST',
        url: ApiClient.getUrl('JellyNext/Notifications/TestEmail'),
        data: JSON.stringify({ to: recipient }),
        contentType: 'application/json'
    }).then(function (response) {
        return response.json().then(function (result) {
            if (!response.ok) {
                throw new Error(result.error || response.statusText);
            }
            return result;
        });
    }).then(function () {
        Dashboard.hideLoadingMsg();
        Dashboard.alert('Test email sent to ' + recipient + '.');
    }).catch(function (error) {
        Dashboard.hideLoadingMsg();
        console.error('Error sending test email:', error);
        Dashboard.alert('Failed to send the test email: ' + (error.message || 'unknown error'));
    });
}
