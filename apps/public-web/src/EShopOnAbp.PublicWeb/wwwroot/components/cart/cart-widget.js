(function () {
    alert("working")
    var connection = new signalR.HubConnectionBuilder()
        .withUrl("/signalr-hubs/basket")
        .build();

    connection.on("BasketProductUpdated", function (data) {
        debugger;
        var widgetManager = $wrapper.data('abp-widget-manager');
        $('#data-cart-count-id').text(data);
        widgetManager.refresh();
        abp.notify.info('The product "' + data.productName + '" has been changed!', 'Your basket has been updated!');

        // $('.basket-list')
        //     .closest('.abp-widget-wrapper')
        //     .each(function(){
        //         var $wrapper = $(this);
        //         if ($wrapper.find('[data-product-id=' + data.productId + ']').length){
        //             var widgetManager = new abp.WidgetManager({
        //                 wrapper: $wrapper
        //             });
        //             widgetManager.refresh();
        //             abp.notify.info('The product "' + data.productName + '" has been changed!', 'Your basket has been updated!');
        //         }
        //     });
    });

    connection.start().then(function () {
    }).catch(function (err) {
        return console.error(err.toString());
    });

})();