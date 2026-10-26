# Nocturne one-click images: one build, one image per marketplace.
#
#   packer init deploy/vm/packer
#   packer build -var version=1.2.3 -only='digitalocean.nocturne' deploy/vm/packer
#
# Every source runs the same provision.sh and cleanup.sh over deploy/vm (the Aspire-generated
# bundle from scripts/publish-release.cs plus the first-boot units), so the clouds differ only in
# the base machine below.

packer {
  required_plugins {
    amazon = {
      source  = "github.com/hashicorp/amazon"
      version = ">= 1.3.0"
    }
    azure = {
      source  = "github.com/hashicorp/azure"
      version = ">= 2.1.0"
    }
    googlecompute = {
      source  = "github.com/hashicorp/googlecompute"
      version = ">= 1.1.0"
    }
    digitalocean = {
      source  = "github.com/digitalocean/digitalocean"
      version = ">= 1.4.0"
    }
  }
}

variable "version" {
  type        = string
  description = "Nocturne release the bundle was generated for, e.g. 1.2.3."
}

variable "digitalocean_api_token" {
  type      = string
  default   = env("DIGITALOCEAN_API_TOKEN")
  sensitive = true
}

variable "digitalocean_region" {
  type    = string
  default = "nyc3"
}

variable "aws_region" {
  type        = string
  default     = "us-east-1"
  description = "AWS Marketplace ingests AMIs from us-east-1 only."
}

variable "azure_subscription_id" {
  type    = string
  default = env("AZURE_SUBSCRIPTION_ID")
}

variable "azure_resource_group" {
  type    = string
  default = "nocturne-images"
}

variable "azure_location" {
  type    = string
  default = "eastus"
}

variable "gcp_project_id" {
  type    = string
  default = env("GOOGLE_CLOUD_PROJECT")
}

variable "gcp_zone" {
  type    = string
  default = "us-central1-a"
}

locals {
  name = "nocturne-${replace(var.version, ".", "-")}"
}

source "digitalocean" "nocturne" {
  api_token = var.digitalocean_api_token
  image     = "ubuntu-24-04-x64"
  region    = var.digitalocean_region
  # The Marketplace offers a snapshot on every size at least as large as the one it was built on.
  size          = "s-1vcpu-1gb"
  ssh_username  = "root"
  snapshot_name = local.name
}

source "amazon-ebs" "x86_64" {
  region        = var.aws_region
  instance_type = "t3.small"
  ami_name      = "${local.name}-x86_64"
  ssh_username  = "ubuntu"
  ena_support   = true
  source_ami_filter {
    filters = {
      name                = "ubuntu/images/hvm-ssd-gp3/ubuntu-noble-24.04-amd64-server-*"
      root-device-type    = "ebs"
      virtualization-type = "hvm"
    }
    owners      = ["099720109477"]
    most_recent = true
  }
}

source "amazon-ebs" "arm64" {
  region        = var.aws_region
  instance_type = "t4g.small"
  ami_name      = "${local.name}-arm64"
  ssh_username  = "ubuntu"
  ena_support   = true
  source_ami_filter {
    filters = {
      name                = "ubuntu/images/hvm-ssd-gp3/ubuntu-noble-24.04-arm64-server-*"
      root-device-type    = "ebs"
      virtualization-type = "hvm"
    }
    owners      = ["099720109477"]
    most_recent = true
  }
}

source "azure-arm" "nocturne" {
  use_azure_cli_auth                = true
  subscription_id                   = var.azure_subscription_id
  os_type                           = "Linux"
  image_publisher                   = "Canonical"
  image_offer                       = "ubuntu-24_04-lts"
  image_sku                         = "server"
  location                          = var.azure_location
  vm_size                           = "Standard_B2s"
  managed_image_name                = local.name
  managed_image_resource_group_name = var.azure_resource_group
}

source "googlecompute" "nocturne" {
  project_id              = var.gcp_project_id
  zone                    = var.gcp_zone
  source_image_family     = "ubuntu-2404-lts-amd64"
  source_image_project_id = ["ubuntu-os-cloud"]
  machine_type            = "e2-small"
  ssh_username            = "packer"
  image_name              = local.name
  image_family            = "nocturne"
}

build {
  sources = [
    "source.digitalocean.nocturne",
    "source.amazon-ebs.x86_64",
    "source.amazon-ebs.arm64",
    "source.azure-arm.nocturne",
    "source.googlecompute.nocturne",
  ]

  provisioner "shell" {
    inline = ["mkdir -p /tmp/nocturne-vm"]
  }

  provisioner "file" {
    sources     = ["${path.root}/../bundle", "${path.root}/../systemd", "${path.root}/../firstboot.sh"]
    destination = "/tmp/nocturne-vm/"
  }

  provisioner "shell" {
    execute_command = "chmod +x {{ .Path }}; sudo -E bash '{{ .Path }}'"
    scripts         = ["${path.root}/provision.sh", "${path.root}/cleanup.sh"]
  }

  # DigitalOcean's own image checker, which its Marketplace review runs as well.
  provisioner "shell" {
    only = ["digitalocean.nocturne"]
    inline = [
      "curl -fsSL https://raw.githubusercontent.com/digitalocean/marketplace-partners/master/scripts/99-img-check.sh | bash",
    ]
  }

  # Must be last: it removes the account Packer is connected as.
  provisioner "shell" {
    only            = ["azure-arm.nocturne"]
    execute_command = "chmod +x {{ .Path }}; sudo -E sh '{{ .Path }}'"
    inline          = ["/usr/sbin/waagent -force -deprovision+user && export HISTSIZE=0 && sync"]
  }
}
