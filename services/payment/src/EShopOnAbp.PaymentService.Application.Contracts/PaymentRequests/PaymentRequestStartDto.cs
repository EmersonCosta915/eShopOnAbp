﻿using System;
using System.ComponentModel.DataAnnotations;

namespace EShopOnAbp.PaymentService.PaymentRequests
{
    [Serializable]
    public class PaymentRequestStartDto
    {
        public Guid PaymentRequestId { get; set; }

        [Required]
        public string ReturnUrl { get; set; }

        public string CancelUrl { get; set; }
    }
}