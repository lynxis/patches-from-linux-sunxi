diff --git a/fs/Kconfig b/fs/Kconfig
index f95ae3a..1dd4948 100644
--- a/fs/Kconfig
+++ b/fs/Kconfig
@@ -203,6 +203,10 @@ source "fs/hfsplus/Kconfig"
 source "fs/befs/Kconfig"
 source "fs/bfs/Kconfig"
 source "fs/efs/Kconfig"
+
+# Patched by YAFFS
+source "fs/yaffs2/Kconfig"
+
 source "fs/jffs2/Kconfig"
 # UBIFS File system configuration
 source "fs/ubifs/Kconfig"
diff --git a/fs/Makefile b/fs/Makefile
index 2fb9779..95cf9de6 100644
--- a/fs/Makefile
+++ b/fs/Makefile
@@ -125,3 +125,6 @@ obj-$(CONFIG_GFS2_FS)           += gfs2/
 obj-y				+= exofs/ # Multiple modules
 obj-$(CONFIG_CEPH_FS)		+= ceph/
 obj-$(CONFIG_PSTORE)		+= pstore/
+
+# Patched by YAFFS
+obj-$(CONFIG_YAFFS_FS)		+= yaffs2/
diff --git a/fs/eventpoll.c b/fs/eventpoll.c
index 33c9599..0863bab 100644
--- a/fs/eventpoll.c
+++ b/fs/eventpoll.c
@@ -33,6 +33,7 @@
 #include <linux/bitops.h>
 #include <linux/mutex.h>
 #include <linux/anon_inodes.h>
+#include <linux/device.h>
 #include <asm/uaccess.h>
 #include <asm/io.h>
 #include <asm/mman.h>
@@ -87,7 +88,7 @@
  */
 
 /* Epoll private bits inside the event mask */
-#define EP_PRIVATE_BITS (EPOLLONESHOT | EPOLLET)
+#define EP_PRIVATE_BITS (EPOLLWAKEUP | EPOLLONESHOT | EPOLLET)
 
 /* Maximum number of nesting allowed inside epoll sets */
 #define EP_MAX_NESTS 4
@@ -154,6 +155,9 @@ struct epitem {
 	/* List header used to link this item to the "struct file" items list */
 	struct list_head fllink;
 
+	/* wakeup_source used when EPOLLWAKEUP is set */
+	struct wakeup_source *ws;
+
 	/* The structure that describe the interested events and the source fd */
 	struct epoll_event event;
 };
@@ -194,6 +198,9 @@ struct eventpoll {
 	 */
 	struct epitem *ovflist;
 
+	/* wakeup_source used when ep_scan_ready_list is running */
+	struct wakeup_source *ws;
+
 	/* The user that created the eventpoll descriptor */
 	struct user_struct *user;
 
@@ -588,8 +595,10 @@ static int ep_scan_ready_list(struct eventpoll *ep,
 		 * queued into ->ovflist but the "txlist" might already
 		 * contain them, and the list_splice() below takes care of them.
 		 */
-		if (!ep_is_linked(&epi->rdllink))
+		if (!ep_is_linked(&epi->rdllink)) {
 			list_add_tail(&epi->rdllink, &ep->rdllist);
+			__pm_stay_awake(epi->ws);
+		}
 	}
 	/*
 	 * We need to set back ep->ovflist to EP_UNACTIVE_PTR, so that after
@@ -602,6 +611,7 @@ static int ep_scan_ready_list(struct eventpoll *ep,
 	 * Quickly re-inject items left on "txlist".
 	 */
 	list_splice(&txlist, &ep->rdllist);
+	__pm_relax(ep->ws);
 
 	if (!list_empty(&ep->rdllist)) {
 		/*
@@ -656,6 +666,8 @@ static int ep_remove(struct eventpoll *ep, struct epitem *epi)
 		list_del_init(&epi->rdllink);
 	spin_unlock_irqrestore(&ep->lock, flags);
 
+	wakeup_source_unregister(epi->ws);
+
 	/* At this point it is safe to free the eventpoll item */
 	kmem_cache_free(epi_cache, epi);
 
@@ -706,6 +718,7 @@ static void ep_free(struct eventpoll *ep)
 	mutex_unlock(&epmutex);
 	mutex_destroy(&ep->mtx);
 	free_uid(ep->user);
+	wakeup_source_unregister(ep->ws);
 	kfree(ep);
 }
 
@@ -737,6 +750,7 @@ static int ep_read_events_proc(struct eventpoll *ep, struct list_head *head,
 			 * callback, but it's not actually ready, as far as
 			 * caller requested events goes. We can remove it here.
 			 */
+			__pm_relax(epi->ws);
 			list_del_init(&epi->rdllink);
 		}
 	}
@@ -927,13 +941,23 @@ static int ep_poll_callback(wait_queue_t *wait, unsigned mode, int sync, void *k
 		if (epi->next == EP_UNACTIVE_PTR) {
 			epi->next = ep->ovflist;
 			ep->ovflist = epi;
+			if (epi->ws) {
+				/*
+				 * Activate ep->ws since epi->ws may get
+				 * deactivated at any time.
+				 */
+				__pm_stay_awake(ep->ws);
+			}
+
 		}
 		goto out_unlock;
 	}
 
 	/* If this file is already in the ready list we exit soon */
-	if (!ep_is_linked(&epi->rdllink))
+	if (!ep_is_linked(&epi->rdllink)) {
 		list_add_tail(&epi->rdllink, &ep->rdllist);
+		__pm_stay_awake(epi->ws);
+	}
 
 	/*
 	 * Wake up ( if active ) both the eventpoll wait list and the ->poll()
@@ -1091,6 +1115,30 @@ static int reverse_path_check(void)
 	return error;
 }
 
+static int ep_create_wakeup_source(struct epitem *epi)
+{
+	const char *name;
+
+	if (!epi->ep->ws) {
+		epi->ep->ws = wakeup_source_register("eventpoll");
+		if (!epi->ep->ws)
+			return -ENOMEM;
+	}
+
+	name = epi->ffd.file->f_path.dentry->d_name.name;
+	epi->ws = wakeup_source_register(name);
+	if (!epi->ws)
+		return -ENOMEM;
+
+	return 0;
+}
+
+static void ep_destroy_wakeup_source(struct epitem *epi)
+{
+	wakeup_source_unregister(epi->ws);
+	epi->ws = NULL;
+}
+
 /*
  * Must be called with "mtx" held.
  */
@@ -1118,6 +1166,13 @@ static int ep_insert(struct eventpoll *ep, struct epoll_event *event,
 	epi->event = *event;
 	epi->nwait = 0;
 	epi->next = EP_UNACTIVE_PTR;
+	if (epi->event.events & EPOLLWAKEUP) {
+		error = ep_create_wakeup_source(epi);
+		if (error)
+			goto error_create_wakeup_source;
+	} else {
+		epi->ws = NULL;
+	}
 
 	/* Initialize the poll table using the queue callback */
 	epq.epi = epi;
@@ -1164,6 +1219,7 @@ static int ep_insert(struct eventpoll *ep, struct epoll_event *event,
 	/* If the file is already "ready" we drop it inside the ready list */
 	if ((revents & event->events) && !ep_is_linked(&epi->rdllink)) {
 		list_add_tail(&epi->rdllink, &ep->rdllist);
+		__pm_stay_awake(epi->ws);
 
 		/* Notify waiting tasks that events are available */
 		if (waitqueue_active(&ep->wq))
@@ -1204,6 +1260,9 @@ error_unregister:
 		list_del_init(&epi->rdllink);
 	spin_unlock_irqrestore(&ep->lock, flags);
 
+	wakeup_source_unregister(epi->ws);
+
+error_create_wakeup_source:
 	kmem_cache_free(epi_cache, epi);
 
 	return error;
@@ -1229,6 +1288,12 @@ static int ep_modify(struct eventpoll *ep, struct epitem *epi, struct epoll_even
 	epi->event.events = event->events; /* need barrier below */
 	pt._key = event->events;
 	epi->event.data = event->data; /* protected by mtx */
+	if (epi->event.events & EPOLLWAKEUP) {
+		if (!epi->ws)
+			ep_create_wakeup_source(epi);
+	} else if (epi->ws) {
+		ep_destroy_wakeup_source(epi);
+	}
 
 	/*
 	 * The following barrier has two effects:
@@ -1264,6 +1329,7 @@ static int ep_modify(struct eventpoll *ep, struct epitem *epi, struct epoll_even
 		spin_lock_irq(&ep->lock);
 		if (!ep_is_linked(&epi->rdllink)) {
 			list_add_tail(&epi->rdllink, &ep->rdllist);
+			__pm_stay_awake(epi->ws);
 
 			/* Notify waiting tasks that events are available */
 			if (waitqueue_active(&ep->wq))
@@ -1302,6 +1368,18 @@ static int ep_send_events_proc(struct eventpoll *ep, struct list_head *head,
 	     !list_empty(head) && eventcnt < esed->maxevents;) {
 		epi = list_first_entry(head, struct epitem, rdllink);
 
+		/*
+		 * Activate ep->ws before deactivating epi->ws to prevent
+		 * triggering auto-suspend here (in case we reactive epi->ws
+		 * below).
+		 *
+		 * This could be rearranged to delay the deactivation of epi->ws
+		 * instead, but then epi->ws would temporarily be out of sync
+		 * with ep_is_linked().
+		 */
+		if (epi->ws && epi->ws->active)
+			__pm_stay_awake(ep->ws);
+		__pm_relax(epi->ws);
 		list_del_init(&epi->rdllink);
 
 		pt._key = epi->event.events;
@@ -1318,6 +1396,7 @@ static int ep_send_events_proc(struct eventpoll *ep, struct list_head *head,
 			if (__put_user(revents, &uevent->events) ||
 			    __put_user(epi->event.data, &uevent->data)) {
 				list_add(&epi->rdllink, head);
+				__pm_stay_awake(epi->ws);
 				return eventcnt ? eventcnt : -EFAULT;
 			}
 			eventcnt++;
@@ -1337,6 +1416,7 @@ static int ep_send_events_proc(struct eventpoll *ep, struct list_head *head,
 				 * poll callback will queue them in ep->ovflist.
 				 */
 				list_add_tail(&epi->rdllink, &ep->rdllist);
+				__pm_stay_awake(epi->ws);
 			}
 		}
 	}
@@ -1649,6 +1729,10 @@ SYSCALL_DEFINE4(epoll_ctl, int, epfd, int, op, int, fd,
 	if (!tfile->f_op || !tfile->f_op->poll)
 		goto error_tgt_fput;
 
+	/* Check if EPOLLWAKEUP is allowed */
+	if ((epds.events & EPOLLWAKEUP) && !capable(CAP_EPOLLWAKEUP))
+		epds.events &= ~EPOLLWAKEUP;
+
 	/*
 	 * We have to check that the file structure underneath the file descriptor
 	 * the user passed to us _is_ an eventpoll file. And also we do not permit
diff --git a/fs/ext4/ialloc.c b/fs/ext4/ialloc.c
index e42b468..15ed025 100644
--- a/fs/ext4/ialloc.c
+++ b/fs/ext4/ialloc.c
@@ -778,7 +778,10 @@ got:
 			ext4_itable_unused_set(sb, gdp,
 					(EXT4_INODES_PER_GROUP(sb) - ino));
 		up_read(&grp->alloc_sem);
+	} else {
+		ext4_lock_group(sb, group);
 	}
+
 	ext4_free_inodes_set(sb, gdp, ext4_free_inodes_count(sb, gdp) - 1);
 	if (S_ISDIR(mode)) {
 		ext4_used_dirs_set(sb, gdp, ext4_used_dirs_count(sb, gdp) + 1);
@@ -790,8 +793,8 @@ got:
 	}
 	if (EXT4_HAS_RO_COMPAT_FEATURE(sb, EXT4_FEATURE_RO_COMPAT_GDT_CSUM)) {
 		gdp->bg_checksum = ext4_group_desc_csum(sbi, group, gdp);
-		ext4_unlock_group(sb, group);
 	}
+	ext4_unlock_group(sb, group);
 
 	BUFFER_TRACE(group_desc_bh, "call ext4_handle_dirty_metadata");
 	err = ext4_handle_dirty_metadata(handle, NULL, group_desc_bh);
diff --git a/fs/fat/dir.c b/fs/fat/dir.c
index aca191b..65e174b 100644
--- a/fs/fat/dir.c
+++ b/fs/fat/dir.c
@@ -754,6 +754,13 @@ static int fat_ioctl_readdir(struct inode *inode, struct file *filp,
 	return ret;
 }
 
+static int fat_ioctl_volume_id(struct inode *dir)
+{
+	struct super_block *sb = dir->i_sb;
+	struct msdos_sb_info *sbi = MSDOS_SB(sb);
+	return sbi->vol_id;
+}
+
 static long fat_dir_ioctl(struct file *filp, unsigned int cmd,
 			  unsigned long arg)
 {
@@ -770,6 +777,8 @@ static long fat_dir_ioctl(struct file *filp, unsigned int cmd,
 		short_only = 0;
 		both = 1;
 		break;
+	case VFAT_IOCTL_GET_VOLUME_ID:
+		return fat_ioctl_volume_id(inode);
 	default:
 		return fat_generic_ioctl(filp, cmd, arg);
 	}
diff --git a/fs/fat/fat.h b/fs/fat/fat.h
index 66994f3..341f753 100644
--- a/fs/fat/fat.h
+++ b/fs/fat/fat.h
@@ -78,6 +78,7 @@ struct msdos_sb_info {
 	const void *dir_ops;		     /* Opaque; default directory operations */
 	int dir_per_block;	     /* dir entries per block */
 	int dir_per_block_bits;	     /* log2(dir_per_block) */
+	unsigned long vol_id;        /* volume ID */
 
 	int fatent_shift;
 	struct fatent_operations *fatent_ops;
diff --git a/fs/fat/inode.c b/fs/fat/inode.c
index 21687e3..d403f76 100644
--- a/fs/fat/inode.c
+++ b/fs/fat/inode.c
@@ -1246,6 +1246,7 @@ int fat_fill_super(struct super_block *sb, void *data, int silent, int isvfat,
 	struct inode *root_inode = NULL, *fat_inode = NULL;
 	struct buffer_head *bh;
 	struct fat_boot_sector *b;
+	struct fat_boot_bsx *bsx;
 	struct msdos_sb_info *sbi;
 	u16 logical_sector_size;
 	u32 total_sectors, total_clusters, fat_clusters, rootdir_sectors;
@@ -1390,6 +1391,8 @@ int fat_fill_super(struct super_block *sb, void *data, int silent, int isvfat,
 			goto out_fail;
 		}
 
+		bsx = (struct fat_boot_bsx *)(bh->b_data + FAT32_BSX_OFFSET);
+
 		fsinfo = (struct fat_boot_fsinfo *)fsinfo_bh->b_data;
 		if (!IS_FSINFO(fsinfo)) {
 			fat_msg(sb, KERN_WARNING, "Invalid FSINFO signature: "
@@ -1405,8 +1408,14 @@ int fat_fill_super(struct super_block *sb, void *data, int silent, int isvfat,
 		}
 
 		brelse(fsinfo_bh);
+	} else {
+		bsx = (struct fat_boot_bsx *)(bh->b_data + FAT16_BSX_OFFSET);
 	}
 
+	/* interpret volume ID as a little endian 32 bit integer */
+	sbi->vol_id = (((u32)bsx->vol_id[0]) | ((u32)bsx->vol_id[1] << 8) |
+		((u32)bsx->vol_id[2] << 16) | ((u32)bsx->vol_id[3] << 24));
+
 	sbi->dir_per_block = sb->s_blocksize / sizeof(struct msdos_dir_entry);
 	sbi->dir_per_block_bits = ffs(sbi->dir_per_block) - 1;
 
diff --git a/fs/fs-writeback.c b/fs/fs-writeback.c
index b35bd64..1b21e0a 100644
--- a/fs/fs-writeback.c
+++ b/fs/fs-writeback.c
@@ -1083,7 +1083,7 @@ void __mark_inode_dirty(struct inode *inode, int flags)
 	if ((inode->i_state & flags) == flags)
 		return;
 
-	if (unlikely(block_dump))
+	if (unlikely(block_dump > 1))
 		block_dump___mark_inode_dirty(inode);
 
 	spin_lock(&inode->i_lock);
diff --git a/fs/fuse/dev.c b/fs/fuse/dev.c
index f4246cf..1f81f70 100644
--- a/fs/fuse/dev.c
+++ b/fs/fuse/dev.c
@@ -19,6 +19,7 @@
 #include <linux/pipe_fs_i.h>
 #include <linux/swap.h>
 #include <linux/splice.h>
+#include <linux/freezer.h>
 
 MODULE_ALIAS_MISCDEV(FUSE_MINOR);
 MODULE_ALIAS("devname:fuse");
@@ -387,7 +388,10 @@ __acquires(fc->lock)
 	 * Wait it out.
 	 */
 	spin_unlock(&fc->lock);
-	wait_event(req->waitq, req->state == FUSE_REQ_FINISHED);
+
+	while (req->state != FUSE_REQ_FINISHED)
+		wait_event_freezable(req->waitq,
+				     req->state == FUSE_REQ_FINISHED);
 	spin_lock(&fc->lock);
 
 	if (!req->aborted)
diff --git a/fs/proc/base.c b/fs/proc/base.c
index 9fc77b4..c8cb15d 100644
--- a/fs/proc/base.c
+++ b/fs/proc/base.c
@@ -137,6 +137,12 @@ struct pid_entry {
 
 static int proc_fd_permission(struct inode *inode, int mask);
 
+/* ANDROID is for special files in /proc. */
+#define ANDROID(NAME, MODE, OTYPE)			\
+	NOD(NAME, (S_IFREG|(MODE)),			\
+		&proc_##OTYPE##_inode_operations,	\
+		&proc_##OTYPE##_operations, {})
+
 /*
  * Count the number of hardlinks for the pid_entry table, excluding the .
  * and .. links.
@@ -969,6 +975,35 @@ out:
 	return err < 0 ? err : count;
 }
 
+static int oom_adjust_permission(struct inode *inode, int mask)
+{
+	uid_t uid;
+	struct task_struct *p;
+
+	p = get_proc_task(inode);
+	if(p) {
+		uid = task_uid(p);
+		put_task_struct(p);
+	}
+
+	/*
+	 * System Server (uid == 1000) is granted access to oom_adj of all 
+	 * android applications (uid > 10000) as and services (uid >= 1000)
+	 */
+	if (p && (current_fsuid() == 1000) && (uid >= 1000)) {
+		if (inode->i_mode >> 6 & mask) {
+			return 0;
+		}
+	}
+
+	/* Fall back to default. */
+	return generic_permission(inode, mask);
+}
+
+static const struct inode_operations proc_oom_adjust_inode_operations = {
+	.permission	= oom_adjust_permission,
+};
+
 static const struct file_operations proc_oom_adjust_operations = {
 	.read		= oom_adjust_read,
 	.write		= oom_adjust_write,
@@ -3011,7 +3046,7 @@ static const struct pid_entry tgid_base_stuff[] = {
 	REG("cgroup",  S_IRUGO, proc_cgroup_operations),
 #endif
 	INF("oom_score",  S_IRUGO, proc_oom_score),
-	REG("oom_adj",    S_IRUGO|S_IWUSR, proc_oom_adjust_operations),
+	ANDROID("oom_adj",S_IRUGO|S_IWUSR, oom_adjust),
 	REG("oom_score_adj", S_IRUGO|S_IWUSR, proc_oom_score_adj_operations),
 #ifdef CONFIG_AUDITSYSCALL
 	REG("loginuid",   S_IWUSR|S_IRUGO, proc_loginuid_operations),
diff --git a/fs/yaffs2/Kconfig b/fs/yaffs2/Kconfig
new file mode 100644
index 0000000..6354140
--- /dev/null
+++ b/fs/yaffs2/Kconfig
@@ -0,0 +1,161 @@
+#
+# YAFFS file system configurations
+#
+
+config YAFFS_FS
+	tristate "YAFFS2 file system support"
+	default n
+	depends on MTD_BLOCK
+	select YAFFS_YAFFS1
+	select YAFFS_YAFFS2
+	help
+	  YAFFS2, or Yet Another Flash Filing System, is a filing system
+	  optimised for NAND Flash chips.
+
+	  To compile the YAFFS2 file system support as a module, choose M
+	  here: the module will be called yaffs2.
+
+	  If unsure, say N.
+
+	  Further information on YAFFS2 is available at
+	  <http://www.aleph1.co.uk/yaffs/>.
+
+config YAFFS_YAFFS1
+	bool "512 byte / page devices"
+	depends on YAFFS_FS
+	default y
+	help
+	  Enable YAFFS1 support -- yaffs for 512 byte / page devices
+
+	  Not needed for 2K-page devices.
+
+	  If unsure, say Y.
+
+config YAFFS_9BYTE_TAGS
+	bool "Use older-style on-NAND data format with pageStatus byte"
+	depends on YAFFS_YAFFS1
+	default n
+	help
+
+	  Older-style on-NAND data format has a "pageStatus" byte to record
+	  chunk/page state.  This byte is zero when the page is discarded.
+	  Choose this option if you have existing on-NAND data using this
+	  format that you need to continue to support.  New data written
+	  also uses the older-style format.  Note: Use of this option
+	  generally requires that MTD's oob layout be adjusted to use the
+	  older-style format.  See notes on tags formats and MTD versions
+	  in yaffs_mtdif1.c.
+
+	  If unsure, say N.
+
+config YAFFS_DOES_ECC
+	bool "Lets Yaffs do its own ECC"
+	depends on YAFFS_FS && YAFFS_YAFFS1 && !YAFFS_9BYTE_TAGS
+	default n
+	help
+	  This enables Yaffs to use its own ECC functions instead of using
+	  the ones from the generic MTD-NAND driver.
+
+	  If unsure, say N.
+
+config YAFFS_ECC_WRONG_ORDER
+	bool "Use the same ecc byte order as Steven Hill's nand_ecc.c"
+	depends on YAFFS_FS && YAFFS_DOES_ECC && !YAFFS_9BYTE_TAGS
+	default n
+	help
+	  This makes yaffs_ecc.c use the same ecc byte order as Steven
+	  Hill's nand_ecc.c. If not set, then you get the same ecc byte
+	  order as SmartMedia.
+
+	  If unsure, say N.
+
+config YAFFS_YAFFS2
+	bool "2048 byte (or larger) / page devices"
+	depends on YAFFS_FS
+	default y
+	help
+	  Enable YAFFS2 support -- yaffs for >= 2K bytes per page devices
+
+	  If unsure, say Y.
+
+config YAFFS_AUTO_YAFFS2
+	bool "Autoselect yaffs2 format"
+	depends on YAFFS_YAFFS2
+	default y
+	help
+	  Without this, you need to explicitely use yaffs2 as the file
+	  system type. With this, you can say "yaffs" and yaffs or yaffs2
+	  will be used depending on the device page size (yaffs on
+	  512-byte page devices, yaffs2 on 2K page devices).
+
+	  If unsure, say Y.
+
+config YAFFS_DISABLE_TAGS_ECC
+	bool "Disable YAFFS from doing ECC on tags by default"
+	depends on YAFFS_FS && YAFFS_YAFFS2
+	default n
+	help
+	  This defaults Yaffs to using its own ECC calculations on tags instead of
+	  just relying on the MTD.
+	  This behavior can also be overridden with tags_ecc_on and
+	  tags_ecc_off mount options.
+
+	  If unsure, say N.
+
+config YAFFS_ALWAYS_CHECK_CHUNK_ERASED
+	bool "Force chunk erase check"
+	depends on YAFFS_FS
+	default n
+	help
+          Normally YAFFS only checks chunks before writing until an erased
+	  chunk is found. This helps to detect any partially written
+	  chunks that might have happened due to power loss.
+
+	  Enabling this forces on the test that chunks are erased in flash
+	  before writing to them. This takes more time but is potentially
+	  a bit more secure.
+
+	  Suggest setting Y during development and ironing out driver
+	  issues etc. Suggest setting to N if you want faster writing.
+
+	  If unsure, say Y.
+
+config YAFFS_EMPTY_LOST_AND_FOUND
+	bool "Empty lost and found on boot"
+	depends on YAFFS_FS
+	default n
+	help
+	  If this is enabled then the contents of lost and found is
+	  automatically dumped at mount.
+
+	  If unsure, say N.
+
+config YAFFS_DISABLE_BLOCK_REFRESHING
+	bool "Disable yaffs2 block refreshing"
+	depends on YAFFS_FS
+	default n
+	help
+	 If this is set, then block refreshing is disabled.
+	 Block refreshing infrequently refreshes the oldest block in
+	 a yaffs2 file system. This mechanism helps to refresh flash to
+	 mitigate against data loss. This is particularly useful for MLC.
+
+	  If unsure, say N.
+
+config YAFFS_DISABLE_BACKGROUND
+	bool "Disable yaffs2 background processing"
+	depends on YAFFS_FS
+	default n
+	help
+	 If this is set, then background processing is disabled.
+	 Background processing makes many foreground activities faster.
+
+	 If unsure, say N.
+
+config YAFFS_XATTR
+	bool "Enable yaffs2 xattr support"
+	depends on YAFFS_FS
+	default y
+	help
+	 If this is set then yaffs2 will provide xattr support.
+	 If unsure, say Y.
diff --git a/fs/yaffs2/Makefile b/fs/yaffs2/Makefile
new file mode 100644
index 0000000..e63a28a
--- /dev/null
+++ b/fs/yaffs2/Makefile
@@ -0,0 +1,17 @@
+#
+# Makefile for the linux YAFFS filesystem routines.
+#
+
+obj-$(CONFIG_YAFFS_FS) += yaffs.o
+
+yaffs-y := yaffs_ecc.o yaffs_vfs.o yaffs_guts.o yaffs_checkptrw.o
+yaffs-y += yaffs_packedtags1.o yaffs_packedtags2.o yaffs_nand.o
+yaffs-y += yaffs_tagscompat.o yaffs_tagsvalidity.o
+yaffs-y += yaffs_mtdif.o yaffs_mtdif1.o yaffs_mtdif2.o
+yaffs-y += yaffs_nameval.o yaffs_attribs.o
+yaffs-y += yaffs_allocator.o
+yaffs-y += yaffs_yaffs1.o
+yaffs-y += yaffs_yaffs2.o
+yaffs-y += yaffs_bitmap.o
+yaffs-y += yaffs_verify.o
+
diff --git a/fs/yaffs2/yaffs_allocator.c b/fs/yaffs2/yaffs_allocator.c
new file mode 100644
index 0000000..f9cd5be
--- /dev/null
+++ b/fs/yaffs2/yaffs_allocator.c
@@ -0,0 +1,396 @@
+/*
+ * YAFFS: Yet Another Flash File System. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU General Public License version 2 as
+ * published by the Free Software Foundation.
+ */
+
+#include "yaffs_allocator.h"
+#include "yaffs_guts.h"
+#include "yaffs_trace.h"
+#include "yportenv.h"
+
+#ifdef CONFIG_YAFFS_KMALLOC_ALLOCATOR
+
+void yaffs_deinit_raw_tnodes_and_objs(struct yaffs_dev *dev)
+{
+	dev = dev;
+}
+
+void yaffs_init_raw_tnodes_and_objs(struct yaffs_dev *dev)
+{
+	dev = dev;
+}
+
+struct yaffs_tnode *yaffs_alloc_raw_tnode(struct yaffs_dev *dev)
+{
+	return (struct yaffs_tnode *)kmalloc(dev->tnode_size, GFP_NOFS);
+}
+
+void yaffs_free_raw_tnode(struct yaffs_dev *dev, struct yaffs_tnode *tn)
+{
+	dev = dev;
+	kfree(tn);
+}
+
+void yaffs_init_raw_objs(struct yaffs_dev *dev)
+{
+	dev = dev;
+}
+
+void yaffs_deinit_raw_objs(struct yaffs_dev *dev)
+{
+	dev = dev;
+}
+
+struct yaffs_obj *yaffs_alloc_raw_obj(struct yaffs_dev *dev)
+{
+	dev = dev;
+	return (struct yaffs_obj *)kmalloc(sizeof(struct yaffs_obj));
+}
+
+void yaffs_free_raw_obj(struct yaffs_dev *dev, struct yaffs_obj *obj)
+{
+
+	dev = dev;
+	kfree(obj);
+}
+
+#else
+
+struct yaffs_tnode_list {
+	struct yaffs_tnode_list *next;
+	struct yaffs_tnode *tnodes;
+};
+
+struct yaffs_obj_list {
+	struct yaffs_obj_list *next;
+	struct yaffs_obj *objects;
+};
+
+struct yaffs_allocator {
+	int n_tnodes_created;
+	struct yaffs_tnode *free_tnodes;
+	int n_free_tnodes;
+	struct yaffs_tnode_list *alloc_tnode_list;
+
+	int n_obj_created;
+	struct yaffs_obj *free_objs;
+	int n_free_objects;
+
+	struct yaffs_obj_list *allocated_obj_list;
+};
+
+static void yaffs_deinit_raw_tnodes(struct yaffs_dev *dev)
+{
+
+	struct yaffs_allocator *allocator =
+	    (struct yaffs_allocator *)dev->allocator;
+
+	struct yaffs_tnode_list *tmp;
+
+	if (!allocator) {
+		YBUG();
+		return;
+	}
+
+	while (allocator->alloc_tnode_list) {
+		tmp = allocator->alloc_tnode_list->next;
+
+		kfree(allocator->alloc_tnode_list->tnodes);
+		kfree(allocator->alloc_tnode_list);
+		allocator->alloc_tnode_list = tmp;
+
+	}
+
+	allocator->free_tnodes = NULL;
+	allocator->n_free_tnodes = 0;
+	allocator->n_tnodes_created = 0;
+}
+
+static void yaffs_init_raw_tnodes(struct yaffs_dev *dev)
+{
+	struct yaffs_allocator *allocator = dev->allocator;
+
+	if (allocator) {
+		allocator->alloc_tnode_list = NULL;
+		allocator->free_tnodes = NULL;
+		allocator->n_free_tnodes = 0;
+		allocator->n_tnodes_created = 0;
+	} else {
+		YBUG();
+	}
+}
+
+static int yaffs_create_tnodes(struct yaffs_dev *dev, int n_tnodes)
+{
+	struct yaffs_allocator *allocator =
+	    (struct yaffs_allocator *)dev->allocator;
+	int i;
+	struct yaffs_tnode *new_tnodes;
+	u8 *mem;
+	struct yaffs_tnode *curr;
+	struct yaffs_tnode *next;
+	struct yaffs_tnode_list *tnl;
+
+	if (!allocator) {
+		YBUG();
+		return YAFFS_FAIL;
+	}
+
+	if (n_tnodes < 1)
+		return YAFFS_OK;
+
+	/* make these things */
+
+	new_tnodes = kmalloc(n_tnodes * dev->tnode_size, GFP_NOFS);
+	mem = (u8 *) new_tnodes;
+
+	if (!new_tnodes) {
+		yaffs_trace(YAFFS_TRACE_ERROR,
+			"yaffs: Could not allocate Tnodes");
+		return YAFFS_FAIL;
+	}
+
+	/* New hookup for wide tnodes */
+	for (i = 0; i < n_tnodes - 1; i++) {
+		curr = (struct yaffs_tnode *)&mem[i * dev->tnode_size];
+		next = (struct yaffs_tnode *)&mem[(i + 1) * dev->tnode_size];
+		curr->internal[0] = next;
+	}
+
+	curr = (struct yaffs_tnode *)&mem[(n_tnodes - 1) * dev->tnode_size];
+	curr->internal[0] = allocator->free_tnodes;
+	allocator->free_tnodes = (struct yaffs_tnode *)mem;
+
+	allocator->n_free_tnodes += n_tnodes;
+	allocator->n_tnodes_created += n_tnodes;
+
+	/* Now add this bunch of tnodes to a list for freeing up.
+	 * NB If we can't add this to the management list it isn't fatal
+	 * but it just means we can't free this bunch of tnodes later.
+	 */
+
+	tnl = kmalloc(sizeof(struct yaffs_tnode_list), GFP_NOFS);
+	if (!tnl) {
+		yaffs_trace(YAFFS_TRACE_ERROR,
+			"Could not add tnodes to management list");
+		return YAFFS_FAIL;
+	} else {
+		tnl->tnodes = new_tnodes;
+		tnl->next = allocator->alloc_tnode_list;
+		allocator->alloc_tnode_list = tnl;
+	}
+
+	yaffs_trace(YAFFS_TRACE_ALLOCATE,"Tnodes added");
+
+	return YAFFS_OK;
+}
+
+struct yaffs_tnode *yaffs_alloc_raw_tnode(struct yaffs_dev *dev)
+{
+	struct yaffs_allocator *allocator =
+	    (struct yaffs_allocator *)dev->allocator;
+	struct yaffs_tnode *tn = NULL;
+
+	if (!allocator) {
+		YBUG();
+		return NULL;
+	}
+
+	/* If there are none left make more */
+	if (!allocator->free_tnodes)
+		yaffs_create_tnodes(dev, YAFFS_ALLOCATION_NTNODES);
+
+	if (allocator->free_tnodes) {
+		tn = allocator->free_tnodes;
+		allocator->free_tnodes = allocator->free_tnodes->internal[0];
+		allocator->n_free_tnodes--;
+	}
+
+	return tn;
+}
+
+/* FreeTnode frees up a tnode and puts it back on the free list */
+void yaffs_free_raw_tnode(struct yaffs_dev *dev, struct yaffs_tnode *tn)
+{
+	struct yaffs_allocator *allocator = dev->allocator;
+
+	if (!allocator) {
+		YBUG();
+		return;
+	}
+
+	if (tn) {
+		tn->internal[0] = allocator->free_tnodes;
+		allocator->free_tnodes = tn;
+		allocator->n_free_tnodes++;
+	}
+	dev->checkpoint_blocks_required = 0;	/* force recalculation */
+}
+
+static void yaffs_init_raw_objs(struct yaffs_dev *dev)
+{
+	struct yaffs_allocator *allocator = dev->allocator;
+
+	if (allocator) {
+		allocator->allocated_obj_list = NULL;
+		allocator->free_objs = NULL;
+		allocator->n_free_objects = 0;
+	} else {
+		YBUG();
+	}
+}
+
+static void yaffs_deinit_raw_objs(struct yaffs_dev *dev)
+{
+	struct yaffs_allocator *allocator = dev->allocator;
+	struct yaffs_obj_list *tmp;
+
+	if (!allocator) {
+		YBUG();
+		return;
+	}
+
+	while (allocator->allocated_obj_list) {
+		tmp = allocator->allocated_obj_list->next;
+		kfree(allocator->allocated_obj_list->objects);
+		kfree(allocator->allocated_obj_list);
+
+		allocator->allocated_obj_list = tmp;
+	}
+
+	allocator->free_objs = NULL;
+	allocator->n_free_objects = 0;
+	allocator->n_obj_created = 0;
+}
+
+static int yaffs_create_free_objs(struct yaffs_dev *dev, int n_obj)
+{
+	struct yaffs_allocator *allocator = dev->allocator;
+
+	int i;
+	struct yaffs_obj *new_objs;
+	struct yaffs_obj_list *list;
+
+	if (!allocator) {
+		YBUG();
+		return YAFFS_FAIL;
+	}
+
+	if (n_obj < 1)
+		return YAFFS_OK;
+
+	/* make these things */
+	new_objs = kmalloc(n_obj * sizeof(struct yaffs_obj), GFP_NOFS);
+	list = kmalloc(sizeof(struct yaffs_obj_list), GFP_NOFS);
+
+	if (!new_objs || !list) {
+		if (new_objs) {
+			kfree(new_objs);
+			new_objs = NULL;
+		}
+		if (list) {
+			kfree(list);
+			list = NULL;
+		}
+		yaffs_trace(YAFFS_TRACE_ALLOCATE,
+			"Could not allocate more objects");
+		return YAFFS_FAIL;
+	}
+
+	/* Hook them into the free list */
+	for (i = 0; i < n_obj - 1; i++) {
+		new_objs[i].siblings.next =
+		    (struct list_head *)(&new_objs[i + 1]);
+	}
+
+	new_objs[n_obj - 1].siblings.next = (void *)allocator->free_objs;
+	allocator->free_objs = new_objs;
+	allocator->n_free_objects += n_obj;
+	allocator->n_obj_created += n_obj;
+
+	/* Now add this bunch of Objects to a list for freeing up. */
+
+	list->objects = new_objs;
+	list->next = allocator->allocated_obj_list;
+	allocator->allocated_obj_list = list;
+
+	return YAFFS_OK;
+}
+
+struct yaffs_obj *yaffs_alloc_raw_obj(struct yaffs_dev *dev)
+{
+	struct yaffs_obj *obj = NULL;
+	struct yaffs_allocator *allocator = dev->allocator;
+
+	if (!allocator) {
+		YBUG();
+		return obj;
+	}
+
+	/* If there are none left make more */
+	if (!allocator->free_objs)
+		yaffs_create_free_objs(dev, YAFFS_ALLOCATION_NOBJECTS);
+
+	if (allocator->free_objs) {
+		obj = allocator->free_objs;
+		allocator->free_objs =
+		    (struct yaffs_obj *)(allocator->free_objs->siblings.next);
+		allocator->n_free_objects--;
+	}
+
+	return obj;
+}
+
+void yaffs_free_raw_obj(struct yaffs_dev *dev, struct yaffs_obj *obj)
+{
+
+	struct yaffs_allocator *allocator = dev->allocator;
+
+	if (!allocator)
+		YBUG();
+	else {
+		/* Link into the free list. */
+		obj->siblings.next = (struct list_head *)(allocator->free_objs);
+		allocator->free_objs = obj;
+		allocator->n_free_objects++;
+	}
+}
+
+void yaffs_deinit_raw_tnodes_and_objs(struct yaffs_dev *dev)
+{
+	if (dev->allocator) {
+		yaffs_deinit_raw_tnodes(dev);
+		yaffs_deinit_raw_objs(dev);
+
+		kfree(dev->allocator);
+		dev->allocator = NULL;
+	} else {
+		YBUG();
+	}
+}
+
+void yaffs_init_raw_tnodes_and_objs(struct yaffs_dev *dev)
+{
+	struct yaffs_allocator *allocator;
+
+	if (!dev->allocator) {
+		allocator = kmalloc(sizeof(struct yaffs_allocator), GFP_NOFS);
+		if (allocator) {
+			dev->allocator = allocator;
+			yaffs_init_raw_tnodes(dev);
+			yaffs_init_raw_objs(dev);
+		}
+	} else {
+		YBUG();
+	}
+}
+
+#endif
diff --git a/fs/yaffs2/yaffs_allocator.h b/fs/yaffs2/yaffs_allocator.h
new file mode 100644
index 0000000..4d5f2ae
--- /dev/null
+++ b/fs/yaffs2/yaffs_allocator.h
@@ -0,0 +1,30 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+#ifndef __YAFFS_ALLOCATOR_H__
+#define __YAFFS_ALLOCATOR_H__
+
+#include "yaffs_guts.h"
+
+void yaffs_init_raw_tnodes_and_objs(struct yaffs_dev *dev);
+void yaffs_deinit_raw_tnodes_and_objs(struct yaffs_dev *dev);
+
+struct yaffs_tnode *yaffs_alloc_raw_tnode(struct yaffs_dev *dev);
+void yaffs_free_raw_tnode(struct yaffs_dev *dev, struct yaffs_tnode *tn);
+
+struct yaffs_obj *yaffs_alloc_raw_obj(struct yaffs_dev *dev);
+void yaffs_free_raw_obj(struct yaffs_dev *dev, struct yaffs_obj *obj);
+
+#endif
diff --git a/fs/yaffs2/yaffs_attribs.c b/fs/yaffs2/yaffs_attribs.c
new file mode 100644
index 0000000..9b47d37
--- /dev/null
+++ b/fs/yaffs2/yaffs_attribs.c
@@ -0,0 +1,124 @@
+/*
+ * YAFFS: Yet Another Flash File System. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU General Public License version 2 as
+ * published by the Free Software Foundation.
+ */
+
+#include "yaffs_guts.h"
+#include "yaffs_attribs.h"
+
+void yaffs_load_attribs(struct yaffs_obj *obj, struct yaffs_obj_hdr *oh)
+{
+	obj->yst_uid = oh->yst_uid;
+	obj->yst_gid = oh->yst_gid;
+	obj->yst_atime = oh->yst_atime;
+	obj->yst_mtime = oh->yst_mtime;
+	obj->yst_ctime = oh->yst_ctime;
+	obj->yst_rdev = oh->yst_rdev;
+}
+
+void yaffs_load_attribs_oh(struct yaffs_obj_hdr *oh, struct yaffs_obj *obj)
+{
+	oh->yst_uid = obj->yst_uid;
+	oh->yst_gid = obj->yst_gid;
+	oh->yst_atime = obj->yst_atime;
+	oh->yst_mtime = obj->yst_mtime;
+	oh->yst_ctime = obj->yst_ctime;
+	oh->yst_rdev = obj->yst_rdev;
+
+}
+
+void yaffs_load_current_time(struct yaffs_obj *obj, int do_a, int do_c)
+{
+	obj->yst_mtime = Y_CURRENT_TIME;
+	if (do_a)
+		obj->yst_atime = obj->yst_mtime;
+	if (do_c)
+		obj->yst_ctime = obj->yst_mtime;
+}
+
+void yaffs_attribs_init(struct yaffs_obj *obj, u32 gid, u32 uid, u32 rdev)
+{
+	yaffs_load_current_time(obj, 1, 1);
+	obj->yst_rdev = rdev;
+	obj->yst_uid = uid;
+	obj->yst_gid = gid;
+}
+
+loff_t yaffs_get_file_size(struct yaffs_obj *obj)
+{
+	YCHAR *alias = NULL;
+	obj = yaffs_get_equivalent_obj(obj);
+
+	switch (obj->variant_type) {
+	case YAFFS_OBJECT_TYPE_FILE:
+		return obj->variant.file_variant.file_size;
+	case YAFFS_OBJECT_TYPE_SYMLINK:
+		alias = obj->variant.symlink_variant.alias;
+		if (!alias)
+			return 0;
+		return strnlen(alias, YAFFS_MAX_ALIAS_LENGTH);
+	default:
+		return 0;
+	}
+}
+
+int yaffs_set_attribs(struct yaffs_obj *obj, struct iattr *attr)
+{
+	unsigned int valid = attr->ia_valid;
+
+	if (valid & ATTR_MODE)
+		obj->yst_mode = attr->ia_mode;
+	if (valid & ATTR_UID)
+		obj->yst_uid = attr->ia_uid;
+	if (valid & ATTR_GID)
+		obj->yst_gid = attr->ia_gid;
+
+	if (valid & ATTR_ATIME)
+		obj->yst_atime = Y_TIME_CONVERT(attr->ia_atime);
+	if (valid & ATTR_CTIME)
+		obj->yst_ctime = Y_TIME_CONVERT(attr->ia_ctime);
+	if (valid & ATTR_MTIME)
+		obj->yst_mtime = Y_TIME_CONVERT(attr->ia_mtime);
+
+	if (valid & ATTR_SIZE)
+		yaffs_resize_file(obj, attr->ia_size);
+
+	yaffs_update_oh(obj, NULL, 1, 0, 0, NULL);
+
+	return YAFFS_OK;
+
+}
+
+int yaffs_get_attribs(struct yaffs_obj *obj, struct iattr *attr)
+{
+	unsigned int valid = 0;
+
+	attr->ia_mode = obj->yst_mode;
+	valid |= ATTR_MODE;
+	attr->ia_uid = obj->yst_uid;
+	valid |= ATTR_UID;
+	attr->ia_gid = obj->yst_gid;
+	valid |= ATTR_GID;
+
+	Y_TIME_CONVERT(attr->ia_atime) = obj->yst_atime;
+	valid |= ATTR_ATIME;
+	Y_TIME_CONVERT(attr->ia_ctime) = obj->yst_ctime;
+	valid |= ATTR_CTIME;
+	Y_TIME_CONVERT(attr->ia_mtime) = obj->yst_mtime;
+	valid |= ATTR_MTIME;
+
+	attr->ia_size = yaffs_get_file_size(obj);
+	valid |= ATTR_SIZE;
+
+	attr->ia_valid = valid;
+
+	return YAFFS_OK;
+}
diff --git a/fs/yaffs2/yaffs_attribs.h b/fs/yaffs2/yaffs_attribs.h
new file mode 100644
index 0000000..33d541d
--- /dev/null
+++ b/fs/yaffs2/yaffs_attribs.h
@@ -0,0 +1,28 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+#ifndef __YAFFS_ATTRIBS_H__
+#define __YAFFS_ATTRIBS_H__
+
+#include "yaffs_guts.h"
+
+void yaffs_load_attribs(struct yaffs_obj *obj, struct yaffs_obj_hdr *oh);
+void yaffs_load_attribs_oh(struct yaffs_obj_hdr *oh, struct yaffs_obj *obj);
+void yaffs_attribs_init(struct yaffs_obj *obj, u32 gid, u32 uid, u32 rdev);
+void yaffs_load_current_time(struct yaffs_obj *obj, int do_a, int do_c);
+int yaffs_set_attribs(struct yaffs_obj *obj, struct iattr *attr);
+int yaffs_get_attribs(struct yaffs_obj *obj, struct iattr *attr);
+
+#endif
diff --git a/fs/yaffs2/yaffs_bitmap.c b/fs/yaffs2/yaffs_bitmap.c
new file mode 100644
index 0000000..7df42cd
--- /dev/null
+++ b/fs/yaffs2/yaffs_bitmap.c
@@ -0,0 +1,98 @@
+/*
+ * YAFFS: Yet Another Flash File System. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU General Public License version 2 as
+ * published by the Free Software Foundation.
+ */
+
+#include "yaffs_bitmap.h"
+#include "yaffs_trace.h"
+/*
+ * Chunk bitmap manipulations
+ */
+
+static inline u8 *yaffs_block_bits(struct yaffs_dev *dev, int blk)
+{
+	if (blk < dev->internal_start_block || blk > dev->internal_end_block) {
+		yaffs_trace(YAFFS_TRACE_ERROR,
+			"BlockBits block %d is not valid",
+			blk);
+		YBUG();
+	}
+	return dev->chunk_bits +
+	    (dev->chunk_bit_stride * (blk - dev->internal_start_block));
+}
+
+void yaffs_verify_chunk_bit_id(struct yaffs_dev *dev, int blk, int chunk)
+{
+	if (blk < dev->internal_start_block || blk > dev->internal_end_block ||
+	    chunk < 0 || chunk >= dev->param.chunks_per_block) {
+		yaffs_trace(YAFFS_TRACE_ERROR,
+			"Chunk Id (%d:%d) invalid",
+			blk, chunk);
+		YBUG();
+	}
+}
+
+void yaffs_clear_chunk_bits(struct yaffs_dev *dev, int blk)
+{
+	u8 *blk_bits = yaffs_block_bits(dev, blk);
+
+	memset(blk_bits, 0, dev->chunk_bit_stride);
+}
+
+void yaffs_clear_chunk_bit(struct yaffs_dev *dev, int blk, int chunk)
+{
+	u8 *blk_bits = yaffs_block_bits(dev, blk);
+
+	yaffs_verify_chunk_bit_id(dev, blk, chunk);
+
+	blk_bits[chunk / 8] &= ~(1 << (chunk & 7));
+}
+
+void yaffs_set_chunk_bit(struct yaffs_dev *dev, int blk, int chunk)
+{
+	u8 *blk_bits = yaffs_block_bits(dev, blk);
+
+	yaffs_verify_chunk_bit_id(dev, blk, chunk);
+
+	blk_bits[chunk / 8] |= (1 << (chunk & 7));
+}
+
+int yaffs_check_chunk_bit(struct yaffs_dev *dev, int blk, int chunk)
+{
+	u8 *blk_bits = yaffs_block_bits(dev, blk);
+	yaffs_verify_chunk_bit_id(dev, blk, chunk);
+
+	return (blk_bits[chunk / 8] & (1 << (chunk & 7))) ? 1 : 0;
+}
+
+int yaffs_still_some_chunks(struct yaffs_dev *dev, int blk)
+{
+	u8 *blk_bits = yaffs_block_bits(dev, blk);
+	int i;
+	for (i = 0; i < dev->chunk_bit_stride; i++) {
+		if (*blk_bits)
+			return 1;
+		blk_bits++;
+	}
+	return 0;
+}
+
+int yaffs_count_chunk_bits(struct yaffs_dev *dev, int blk)
+{
+	u8 *blk_bits = yaffs_block_bits(dev, blk);
+	int i;
+	int n = 0;
+
+	for (i = 0; i < dev->chunk_bit_stride; i++, blk_bits++)
+		n += hweight8(*blk_bits);
+
+	return n;
+}
diff --git a/fs/yaffs2/yaffs_bitmap.h b/fs/yaffs2/yaffs_bitmap.h
new file mode 100644
index 0000000..cf9ea58
--- /dev/null
+++ b/fs/yaffs2/yaffs_bitmap.h
@@ -0,0 +1,33 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+/*
+ * Chunk bitmap manipulations
+ */
+
+#ifndef __YAFFS_BITMAP_H__
+#define __YAFFS_BITMAP_H__
+
+#include "yaffs_guts.h"
+
+void yaffs_verify_chunk_bit_id(struct yaffs_dev *dev, int blk, int chunk);
+void yaffs_clear_chunk_bits(struct yaffs_dev *dev, int blk);
+void yaffs_clear_chunk_bit(struct yaffs_dev *dev, int blk, int chunk);
+void yaffs_set_chunk_bit(struct yaffs_dev *dev, int blk, int chunk);
+int yaffs_check_chunk_bit(struct yaffs_dev *dev, int blk, int chunk);
+int yaffs_still_some_chunks(struct yaffs_dev *dev, int blk);
+int yaffs_count_chunk_bits(struct yaffs_dev *dev, int blk);
+
+#endif
diff --git a/fs/yaffs2/yaffs_checkptrw.c b/fs/yaffs2/yaffs_checkptrw.c
new file mode 100644
index 0000000..4e40f43
--- /dev/null
+++ b/fs/yaffs2/yaffs_checkptrw.c
@@ -0,0 +1,415 @@
+/*
+ * YAFFS: Yet Another Flash File System. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU General Public License version 2 as
+ * published by the Free Software Foundation.
+ */
+
+#include "yaffs_checkptrw.h"
+#include "yaffs_getblockinfo.h"
+
+static int yaffs2_checkpt_space_ok(struct yaffs_dev *dev)
+{
+	int blocks_avail = dev->n_erased_blocks - dev->param.n_reserved_blocks;
+
+	yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+		"checkpt blocks_avail = %d", blocks_avail);
+
+	return (blocks_avail <= 0) ? 0 : 1;
+}
+
+static int yaffs_checkpt_erase(struct yaffs_dev *dev)
+{
+	int i;
+
+	if (!dev->param.erase_fn)
+		return 0;
+	yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+		"checking blocks %d to %d",
+		dev->internal_start_block, dev->internal_end_block);
+
+	for (i = dev->internal_start_block; i <= dev->internal_end_block; i++) {
+		struct yaffs_block_info *bi = yaffs_get_block_info(dev, i);
+		if (bi->block_state == YAFFS_BLOCK_STATE_CHECKPOINT) {
+			yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+			"erasing checkpt block %d", i);
+
+			dev->n_erasures++;
+
+			if (dev->param.
+			    erase_fn(dev,
+				     i - dev->block_offset /* realign */ )) {
+				bi->block_state = YAFFS_BLOCK_STATE_EMPTY;
+				dev->n_erased_blocks++;
+				dev->n_free_chunks +=
+				    dev->param.chunks_per_block;
+			} else {
+				dev->param.bad_block_fn(dev, i);
+				bi->block_state = YAFFS_BLOCK_STATE_DEAD;
+			}
+		}
+	}
+
+	dev->blocks_in_checkpt = 0;
+
+	return 1;
+}
+
+static void yaffs2_checkpt_find_erased_block(struct yaffs_dev *dev)
+{
+	int i;
+	int blocks_avail = dev->n_erased_blocks - dev->param.n_reserved_blocks;
+	yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+		"allocating checkpt block: erased %d reserved %d avail %d next %d ",
+		dev->n_erased_blocks, dev->param.n_reserved_blocks,
+		blocks_avail, dev->checkpt_next_block);
+
+	if (dev->checkpt_next_block >= 0 &&
+	    dev->checkpt_next_block <= dev->internal_end_block &&
+	    blocks_avail > 0) {
+
+		for (i = dev->checkpt_next_block; i <= dev->internal_end_block;
+		     i++) {
+			struct yaffs_block_info *bi =
+			    yaffs_get_block_info(dev, i);
+			if (bi->block_state == YAFFS_BLOCK_STATE_EMPTY) {
+				dev->checkpt_next_block = i + 1;
+				dev->checkpt_cur_block = i;
+				yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+					"allocating checkpt block %d", i);
+				return;
+			}
+		}
+	}
+	yaffs_trace(YAFFS_TRACE_CHECKPOINT, "out of checkpt blocks");
+
+	dev->checkpt_next_block = -1;
+	dev->checkpt_cur_block = -1;
+}
+
+static void yaffs2_checkpt_find_block(struct yaffs_dev *dev)
+{
+	int i;
+	struct yaffs_ext_tags tags;
+
+	yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+		"find next checkpt block: start:  blocks %d next %d",
+		dev->blocks_in_checkpt, dev->checkpt_next_block);
+
+	if (dev->blocks_in_checkpt < dev->checkpt_max_blocks)
+		for (i = dev->checkpt_next_block; i <= dev->internal_end_block;
+		     i++) {
+			int chunk = i * dev->param.chunks_per_block;
+			int realigned_chunk = chunk - dev->chunk_offset;
+
+			dev->param.read_chunk_tags_fn(dev, realigned_chunk,
+						      NULL, &tags);
+			yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+				"find next checkpt block: search: block %d oid %d seq %d eccr %d",
+				i, tags.obj_id, tags.seq_number,
+				tags.ecc_result);
+
+			if (tags.seq_number == YAFFS_SEQUENCE_CHECKPOINT_DATA) {
+				/* Right kind of block */
+				dev->checkpt_next_block = tags.obj_id;
+				dev->checkpt_cur_block = i;
+				dev->checkpt_block_list[dev->
+							blocks_in_checkpt] = i;
+				dev->blocks_in_checkpt++;
+				yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+					"found checkpt block %d", i);
+				return;
+			}
+		}
+
+	yaffs_trace(YAFFS_TRACE_CHECKPOINT, "found no more checkpt blocks");
+
+	dev->checkpt_next_block = -1;
+	dev->checkpt_cur_block = -1;
+}
+
+int yaffs2_checkpt_open(struct yaffs_dev *dev, int writing)
+{
+
+	dev->checkpt_open_write = writing;
+
+	/* Got the functions we need? */
+	if (!dev->param.write_chunk_tags_fn ||
+	    !dev->param.read_chunk_tags_fn ||
+	    !dev->param.erase_fn || !dev->param.bad_block_fn)
+		return 0;
+
+	if (writing && !yaffs2_checkpt_space_ok(dev))
+		return 0;
+
+	if (!dev->checkpt_buffer)
+		dev->checkpt_buffer =
+		    kmalloc(dev->param.total_bytes_per_chunk, GFP_NOFS);
+	if (!dev->checkpt_buffer)
+		return 0;
+
+	dev->checkpt_page_seq = 0;
+	dev->checkpt_byte_count = 0;
+	dev->checkpt_sum = 0;
+	dev->checkpt_xor = 0;
+	dev->checkpt_cur_block = -1;
+	dev->checkpt_cur_chunk = -1;
+	dev->checkpt_next_block = dev->internal_start_block;
+
+	/* Erase all the blocks in the checkpoint area */
+	if (writing) {
+		memset(dev->checkpt_buffer, 0, dev->data_bytes_per_chunk);
+		dev->checkpt_byte_offs = 0;
+		return yaffs_checkpt_erase(dev);
+	} else {
+		int i;
+		/* Set to a value that will kick off a read */
+		dev->checkpt_byte_offs = dev->data_bytes_per_chunk;
+		/* A checkpoint block list of 1 checkpoint block per 16 block is (hopefully)
+		 * going to be way more than we need */
+		dev->blocks_in_checkpt = 0;
+		dev->checkpt_max_blocks =
+		    (dev->internal_end_block - dev->internal_start_block) / 16 +
+		    2;
+		dev->checkpt_block_list =
+		    kmalloc(sizeof(int) * dev->checkpt_max_blocks, GFP_NOFS);
+		if (!dev->checkpt_block_list)
+			return 0;
+
+		for (i = 0; i < dev->checkpt_max_blocks; i++)
+			dev->checkpt_block_list[i] = -1;
+	}
+
+	return 1;
+}
+
+int yaffs2_get_checkpt_sum(struct yaffs_dev *dev, u32 * sum)
+{
+	u32 composite_sum;
+	composite_sum = (dev->checkpt_sum << 8) | (dev->checkpt_xor & 0xFF);
+	*sum = composite_sum;
+	return 1;
+}
+
+static int yaffs2_checkpt_flush_buffer(struct yaffs_dev *dev)
+{
+	int chunk;
+	int realigned_chunk;
+
+	struct yaffs_ext_tags tags;
+
+	if (dev->checkpt_cur_block < 0) {
+		yaffs2_checkpt_find_erased_block(dev);
+		dev->checkpt_cur_chunk = 0;
+	}
+
+	if (dev->checkpt_cur_block < 0)
+		return 0;
+
+	tags.is_deleted = 0;
+	tags.obj_id = dev->checkpt_next_block;	/* Hint to next place to look */
+	tags.chunk_id = dev->checkpt_page_seq + 1;
+	tags.seq_number = YAFFS_SEQUENCE_CHECKPOINT_DATA;
+	tags.n_bytes = dev->data_bytes_per_chunk;
+	if (dev->checkpt_cur_chunk == 0) {
+		/* First chunk we write for the block? Set block state to
+		   checkpoint */
+		struct yaffs_block_info *bi =
+		    yaffs_get_block_info(dev, dev->checkpt_cur_block);
+		bi->block_state = YAFFS_BLOCK_STATE_CHECKPOINT;
+		dev->blocks_in_checkpt++;
+	}
+
+	chunk =
+	    dev->checkpt_cur_block * dev->param.chunks_per_block +
+	    dev->checkpt_cur_chunk;
+
+	yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+		"checkpoint wite buffer nand %d(%d:%d) objid %d chId %d",
+		chunk, dev->checkpt_cur_block, dev->checkpt_cur_chunk,
+		tags.obj_id, tags.chunk_id);
+
+	realigned_chunk = chunk - dev->chunk_offset;
+
+	dev->n_page_writes++;
+
+	dev->param.write_chunk_tags_fn(dev, realigned_chunk,
+				       dev->checkpt_buffer, &tags);
+	dev->checkpt_byte_offs = 0;
+	dev->checkpt_page_seq++;
+	dev->checkpt_cur_chunk++;
+	if (dev->checkpt_cur_chunk >= dev->param.chunks_per_block) {
+		dev->checkpt_cur_chunk = 0;
+		dev->checkpt_cur_block = -1;
+	}
+	memset(dev->checkpt_buffer, 0, dev->data_bytes_per_chunk);
+
+	return 1;
+}
+
+int yaffs2_checkpt_wr(struct yaffs_dev *dev, const void *data, int n_bytes)
+{
+	int i = 0;
+	int ok = 1;
+
+	u8 *data_bytes = (u8 *) data;
+
+	if (!dev->checkpt_buffer)
+		return 0;
+
+	if (!dev->checkpt_open_write)
+		return -1;
+
+	while (i < n_bytes && ok) {
+		dev->checkpt_buffer[dev->checkpt_byte_offs] = *data_bytes;
+		dev->checkpt_sum += *data_bytes;
+		dev->checkpt_xor ^= *data_bytes;
+
+		dev->checkpt_byte_offs++;
+		i++;
+		data_bytes++;
+		dev->checkpt_byte_count++;
+
+		if (dev->checkpt_byte_offs < 0 ||
+		    dev->checkpt_byte_offs >= dev->data_bytes_per_chunk)
+			ok = yaffs2_checkpt_flush_buffer(dev);
+	}
+
+	return i;
+}
+
+int yaffs2_checkpt_rd(struct yaffs_dev *dev, void *data, int n_bytes)
+{
+	int i = 0;
+	int ok = 1;
+	struct yaffs_ext_tags tags;
+
+	int chunk;
+	int realigned_chunk;
+
+	u8 *data_bytes = (u8 *) data;
+
+	if (!dev->checkpt_buffer)
+		return 0;
+
+	if (dev->checkpt_open_write)
+		return -1;
+
+	while (i < n_bytes && ok) {
+
+		if (dev->checkpt_byte_offs < 0 ||
+		    dev->checkpt_byte_offs >= dev->data_bytes_per_chunk) {
+
+			if (dev->checkpt_cur_block < 0) {
+				yaffs2_checkpt_find_block(dev);
+				dev->checkpt_cur_chunk = 0;
+			}
+
+			if (dev->checkpt_cur_block < 0)
+				ok = 0;
+			else {
+				chunk = dev->checkpt_cur_block *
+				    dev->param.chunks_per_block +
+				    dev->checkpt_cur_chunk;
+
+				realigned_chunk = chunk - dev->chunk_offset;
+
+				dev->n_page_reads++;
+
+				/* read in the next chunk */
+				dev->param.read_chunk_tags_fn(dev,
+							      realigned_chunk,
+							      dev->
+							      checkpt_buffer,
+							      &tags);
+
+				if (tags.chunk_id != (dev->checkpt_page_seq + 1)
+				    || tags.ecc_result > YAFFS_ECC_RESULT_FIXED
+				    || tags.seq_number !=
+				    YAFFS_SEQUENCE_CHECKPOINT_DATA)
+					ok = 0;
+
+				dev->checkpt_byte_offs = 0;
+				dev->checkpt_page_seq++;
+				dev->checkpt_cur_chunk++;
+
+				if (dev->checkpt_cur_chunk >=
+				    dev->param.chunks_per_block)
+					dev->checkpt_cur_block = -1;
+			}
+		}
+
+		if (ok) {
+			*data_bytes =
+			    dev->checkpt_buffer[dev->checkpt_byte_offs];
+			dev->checkpt_sum += *data_bytes;
+			dev->checkpt_xor ^= *data_bytes;
+			dev->checkpt_byte_offs++;
+			i++;
+			data_bytes++;
+			dev->checkpt_byte_count++;
+		}
+	}
+
+	return i;
+}
+
+int yaffs_checkpt_close(struct yaffs_dev *dev)
+{
+
+	if (dev->checkpt_open_write) {
+		if (dev->checkpt_byte_offs != 0)
+			yaffs2_checkpt_flush_buffer(dev);
+	} else if (dev->checkpt_block_list) {
+		int i;
+		for (i = 0;
+		     i < dev->blocks_in_checkpt
+		     && dev->checkpt_block_list[i] >= 0; i++) {
+			int blk = dev->checkpt_block_list[i];
+			struct yaffs_block_info *bi = NULL;
+			if (dev->internal_start_block <= blk
+			    && blk <= dev->internal_end_block)
+				bi = yaffs_get_block_info(dev, blk);
+			if (bi && bi->block_state == YAFFS_BLOCK_STATE_EMPTY)
+				bi->block_state = YAFFS_BLOCK_STATE_CHECKPOINT;
+			else {
+				/* Todo this looks odd... */
+			}
+		}
+		kfree(dev->checkpt_block_list);
+		dev->checkpt_block_list = NULL;
+	}
+
+	dev->n_free_chunks -=
+	    dev->blocks_in_checkpt * dev->param.chunks_per_block;
+	dev->n_erased_blocks -= dev->blocks_in_checkpt;
+
+	yaffs_trace(YAFFS_TRACE_CHECKPOINT,"checkpoint byte count %d",
+		dev->checkpt_byte_count);
+
+	if (dev->checkpt_buffer) {
+		/* free the buffer */
+		kfree(dev->checkpt_buffer);
+		dev->checkpt_buffer = NULL;
+		return 1;
+	} else {
+		return 0;
+        }
+}
+
+int yaffs2_checkpt_invalidate_stream(struct yaffs_dev *dev)
+{
+	/* Erase the checkpoint data */
+
+	yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+		"checkpoint invalidate of %d blocks",
+		dev->blocks_in_checkpt);
+
+	return yaffs_checkpt_erase(dev);
+}
diff --git a/fs/yaffs2/yaffs_checkptrw.h b/fs/yaffs2/yaffs_checkptrw.h
new file mode 100644
index 0000000..361c606
--- /dev/null
+++ b/fs/yaffs2/yaffs_checkptrw.h
@@ -0,0 +1,33 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+#ifndef __YAFFS_CHECKPTRW_H__
+#define __YAFFS_CHECKPTRW_H__
+
+#include "yaffs_guts.h"
+
+int yaffs2_checkpt_open(struct yaffs_dev *dev, int writing);
+
+int yaffs2_checkpt_wr(struct yaffs_dev *dev, const void *data, int n_bytes);
+
+int yaffs2_checkpt_rd(struct yaffs_dev *dev, void *data, int n_bytes);
+
+int yaffs2_get_checkpt_sum(struct yaffs_dev *dev, u32 * sum);
+
+int yaffs_checkpt_close(struct yaffs_dev *dev);
+
+int yaffs2_checkpt_invalidate_stream(struct yaffs_dev *dev);
+
+#endif
diff --git a/fs/yaffs2/yaffs_ecc.c b/fs/yaffs2/yaffs_ecc.c
new file mode 100644
index 0000000..e95a806
--- /dev/null
+++ b/fs/yaffs2/yaffs_ecc.c
@@ -0,0 +1,298 @@
+/*
+ * YAFFS: Yet Another Flash File System. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU General Public License version 2 as
+ * published by the Free Software Foundation.
+ */
+
+/*
+ * This code implements the ECC algorithm used in SmartMedia.
+ *
+ * The ECC comprises 22 bits of parity information and is stuffed into 3 bytes.
+ * The two unused bit are set to 1.
+ * The ECC can correct single bit errors in a 256-byte page of data. Thus, two such ECC
+ * blocks are used on a 512-byte NAND page.
+ *
+ */
+
+/* Table generated by gen-ecc.c
+ * Using a table means we do not have to calculate p1..p4 and p1'..p4'
+ * for each byte of data. These are instead provided in a table in bits7..2.
+ * Bit 0 of each entry indicates whether the entry has an odd or even parity, and therefore
+ * this bytes influence on the line parity.
+ */
+
+#include "yportenv.h"
+
+#include "yaffs_ecc.h"
+
+static const unsigned char column_parity_table[] = {
+	0x00, 0x55, 0x59, 0x0c, 0x65, 0x30, 0x3c, 0x69,
+	0x69, 0x3c, 0x30, 0x65, 0x0c, 0x59, 0x55, 0x00,
+	0x95, 0xc0, 0xcc, 0x99, 0xf0, 0xa5, 0xa9, 0xfc,
+	0xfc, 0xa9, 0xa5, 0xf0, 0x99, 0xcc, 0xc0, 0x95,
+	0x99, 0xcc, 0xc0, 0x95, 0xfc, 0xa9, 0xa5, 0xf0,
+	0xf0, 0xa5, 0xa9, 0xfc, 0x95, 0xc0, 0xcc, 0x99,
+	0x0c, 0x59, 0x55, 0x00, 0x69, 0x3c, 0x30, 0x65,
+	0x65, 0x30, 0x3c, 0x69, 0x00, 0x55, 0x59, 0x0c,
+	0xa5, 0xf0, 0xfc, 0xa9, 0xc0, 0x95, 0x99, 0xcc,
+	0xcc, 0x99, 0x95, 0xc0, 0xa9, 0xfc, 0xf0, 0xa5,
+	0x30, 0x65, 0x69, 0x3c, 0x55, 0x00, 0x0c, 0x59,
+	0x59, 0x0c, 0x00, 0x55, 0x3c, 0x69, 0x65, 0x30,
+	0x3c, 0x69, 0x65, 0x30, 0x59, 0x0c, 0x00, 0x55,
+	0x55, 0x00, 0x0c, 0x59, 0x30, 0x65, 0x69, 0x3c,
+	0xa9, 0xfc, 0xf0, 0xa5, 0xcc, 0x99, 0x95, 0xc0,
+	0xc0, 0x95, 0x99, 0xcc, 0xa5, 0xf0, 0xfc, 0xa9,
+	0xa9, 0xfc, 0xf0, 0xa5, 0xcc, 0x99, 0x95, 0xc0,
+	0xc0, 0x95, 0x99, 0xcc, 0xa5, 0xf0, 0xfc, 0xa9,
+	0x3c, 0x69, 0x65, 0x30, 0x59, 0x0c, 0x00, 0x55,
+	0x55, 0x00, 0x0c, 0x59, 0x30, 0x65, 0x69, 0x3c,
+	0x30, 0x65, 0x69, 0x3c, 0x55, 0x00, 0x0c, 0x59,
+	0x59, 0x0c, 0x00, 0x55, 0x3c, 0x69, 0x65, 0x30,
+	0xa5, 0xf0, 0xfc, 0xa9, 0xc0, 0x95, 0x99, 0xcc,
+	0xcc, 0x99, 0x95, 0xc0, 0xa9, 0xfc, 0xf0, 0xa5,
+	0x0c, 0x59, 0x55, 0x00, 0x69, 0x3c, 0x30, 0x65,
+	0x65, 0x30, 0x3c, 0x69, 0x00, 0x55, 0x59, 0x0c,
+	0x99, 0xcc, 0xc0, 0x95, 0xfc, 0xa9, 0xa5, 0xf0,
+	0xf0, 0xa5, 0xa9, 0xfc, 0x95, 0xc0, 0xcc, 0x99,
+	0x95, 0xc0, 0xcc, 0x99, 0xf0, 0xa5, 0xa9, 0xfc,
+	0xfc, 0xa9, 0xa5, 0xf0, 0x99, 0xcc, 0xc0, 0x95,
+	0x00, 0x55, 0x59, 0x0c, 0x65, 0x30, 0x3c, 0x69,
+	0x69, 0x3c, 0x30, 0x65, 0x0c, 0x59, 0x55, 0x00,
+};
+
+
+/* Calculate the ECC for a 256-byte block of data */
+void yaffs_ecc_cacl(const unsigned char *data, unsigned char *ecc)
+{
+	unsigned int i;
+
+	unsigned char col_parity = 0;
+	unsigned char line_parity = 0;
+	unsigned char line_parity_prime = 0;
+	unsigned char t;
+	unsigned char b;
+
+	for (i = 0; i < 256; i++) {
+		b = column_parity_table[*data++];
+		col_parity ^= b;
+
+		if (b & 0x01) {	/* odd number of bits in the byte */
+			line_parity ^= i;
+			line_parity_prime ^= ~i;
+		}
+	}
+
+	ecc[2] = (~col_parity) | 0x03;
+
+	t = 0;
+	if (line_parity & 0x80)
+		t |= 0x80;
+	if (line_parity_prime & 0x80)
+		t |= 0x40;
+	if (line_parity & 0x40)
+		t |= 0x20;
+	if (line_parity_prime & 0x40)
+		t |= 0x10;
+	if (line_parity & 0x20)
+		t |= 0x08;
+	if (line_parity_prime & 0x20)
+		t |= 0x04;
+	if (line_parity & 0x10)
+		t |= 0x02;
+	if (line_parity_prime & 0x10)
+		t |= 0x01;
+	ecc[1] = ~t;
+
+	t = 0;
+	if (line_parity & 0x08)
+		t |= 0x80;
+	if (line_parity_prime & 0x08)
+		t |= 0x40;
+	if (line_parity & 0x04)
+		t |= 0x20;
+	if (line_parity_prime & 0x04)
+		t |= 0x10;
+	if (line_parity & 0x02)
+		t |= 0x08;
+	if (line_parity_prime & 0x02)
+		t |= 0x04;
+	if (line_parity & 0x01)
+		t |= 0x02;
+	if (line_parity_prime & 0x01)
+		t |= 0x01;
+	ecc[0] = ~t;
+
+#ifdef CONFIG_YAFFS_ECC_WRONG_ORDER
+	/* Swap the bytes into the wrong order */
+	t = ecc[0];
+	ecc[0] = ecc[1];
+	ecc[1] = t;
+#endif
+}
+
+/* Correct the ECC on a 256 byte block of data */
+
+int yaffs_ecc_correct(unsigned char *data, unsigned char *read_ecc,
+		      const unsigned char *test_ecc)
+{
+	unsigned char d0, d1, d2;	/* deltas */
+
+	d0 = read_ecc[0] ^ test_ecc[0];
+	d1 = read_ecc[1] ^ test_ecc[1];
+	d2 = read_ecc[2] ^ test_ecc[2];
+
+	if ((d0 | d1 | d2) == 0)
+		return 0;	/* no error */
+
+	if (((d0 ^ (d0 >> 1)) & 0x55) == 0x55 &&
+	    ((d1 ^ (d1 >> 1)) & 0x55) == 0x55 &&
+	    ((d2 ^ (d2 >> 1)) & 0x54) == 0x54) {
+		/* Single bit (recoverable) error in data */
+
+		unsigned byte;
+		unsigned bit;
+
+#ifdef CONFIG_YAFFS_ECC_WRONG_ORDER
+		/* swap the bytes to correct for the wrong order */
+		unsigned char t;
+
+		t = d0;
+		d0 = d1;
+		d1 = t;
+#endif
+
+		bit = byte = 0;
+
+		if (d1 & 0x80)
+			byte |= 0x80;
+		if (d1 & 0x20)
+			byte |= 0x40;
+		if (d1 & 0x08)
+			byte |= 0x20;
+		if (d1 & 0x02)
+			byte |= 0x10;
+		if (d0 & 0x80)
+			byte |= 0x08;
+		if (d0 & 0x20)
+			byte |= 0x04;
+		if (d0 & 0x08)
+			byte |= 0x02;
+		if (d0 & 0x02)
+			byte |= 0x01;
+
+		if (d2 & 0x80)
+			bit |= 0x04;
+		if (d2 & 0x20)
+			bit |= 0x02;
+		if (d2 & 0x08)
+			bit |= 0x01;
+
+		data[byte] ^= (1 << bit);
+
+		return 1;	/* Corrected the error */
+	}
+
+	if ((hweight8(d0) + hweight8(d1) + hweight8(d2)) == 1) {
+		/* Reccoverable error in ecc */
+
+		read_ecc[0] = test_ecc[0];
+		read_ecc[1] = test_ecc[1];
+		read_ecc[2] = test_ecc[2];
+
+		return 1;	/* Corrected the error */
+	}
+
+	/* Unrecoverable error */
+
+	return -1;
+
+}
+
+/*
+ * ECCxxxOther does ECC calcs on arbitrary n bytes of data
+ */
+void yaffs_ecc_calc_other(const unsigned char *data, unsigned n_bytes,
+			  struct yaffs_ecc_other *ecc_other)
+{
+	unsigned int i;
+
+	unsigned char col_parity = 0;
+	unsigned line_parity = 0;
+	unsigned line_parity_prime = 0;
+	unsigned char b;
+
+	for (i = 0; i < n_bytes; i++) {
+		b = column_parity_table[*data++];
+		col_parity ^= b;
+
+		if (b & 0x01) {
+			/* odd number of bits in the byte */
+			line_parity ^= i;
+			line_parity_prime ^= ~i;
+		}
+
+	}
+
+	ecc_other->col_parity = (col_parity >> 2) & 0x3f;
+	ecc_other->line_parity = line_parity;
+	ecc_other->line_parity_prime = line_parity_prime;
+}
+
+int yaffs_ecc_correct_other(unsigned char *data, unsigned n_bytes,
+			    struct yaffs_ecc_other *read_ecc,
+			    const struct yaffs_ecc_other *test_ecc)
+{
+	unsigned char delta_col;	/* column parity delta */
+	unsigned delta_line;	/* line parity delta */
+	unsigned delta_line_prime;	/* line parity delta */
+	unsigned bit;
+
+	delta_col = read_ecc->col_parity ^ test_ecc->col_parity;
+	delta_line = read_ecc->line_parity ^ test_ecc->line_parity;
+	delta_line_prime =
+	    read_ecc->line_parity_prime ^ test_ecc->line_parity_prime;
+
+	if ((delta_col | delta_line | delta_line_prime) == 0)
+		return 0;	/* no error */
+
+	if (delta_line == ~delta_line_prime &&
+	    (((delta_col ^ (delta_col >> 1)) & 0x15) == 0x15)) {
+		/* Single bit (recoverable) error in data */
+
+		bit = 0;
+
+		if (delta_col & 0x20)
+			bit |= 0x04;
+		if (delta_col & 0x08)
+			bit |= 0x02;
+		if (delta_col & 0x02)
+			bit |= 0x01;
+
+		if (delta_line >= n_bytes)
+			return -1;
+
+		data[delta_line] ^= (1 << bit);
+
+		return 1;	/* corrected */
+	}
+
+	if ((hweight32(delta_line) +
+	     hweight32(delta_line_prime) +
+	     hweight8(delta_col)) == 1) {
+		/* Reccoverable error in ecc */
+
+		*read_ecc = *test_ecc;
+		return 1;	/* corrected */
+	}
+
+	/* Unrecoverable error */
+
+	return -1;
+}
diff --git a/fs/yaffs2/yaffs_ecc.h b/fs/yaffs2/yaffs_ecc.h
new file mode 100644
index 0000000..b0c461d
--- /dev/null
+++ b/fs/yaffs2/yaffs_ecc.h
@@ -0,0 +1,44 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+/*
+ * This code implements the ECC algorithm used in SmartMedia.
+ *
+ * The ECC comprises 22 bits of parity information and is stuffed into 3 bytes.
+ * The two unused bit are set to 1.
+ * The ECC can correct single bit errors in a 256-byte page of data. Thus, two such ECC
+ * blocks are used on a 512-byte NAND page.
+ *
+ */
+
+#ifndef __YAFFS_ECC_H__
+#define __YAFFS_ECC_H__
+
+struct yaffs_ecc_other {
+	unsigned char col_parity;
+	unsigned line_parity;
+	unsigned line_parity_prime;
+};
+
+void yaffs_ecc_cacl(const unsigned char *data, unsigned char *ecc);
+int yaffs_ecc_correct(unsigned char *data, unsigned char *read_ecc,
+		      const unsigned char *test_ecc);
+
+void yaffs_ecc_calc_other(const unsigned char *data, unsigned n_bytes,
+			  struct yaffs_ecc_other *ecc);
+int yaffs_ecc_correct_other(unsigned char *data, unsigned n_bytes,
+			    struct yaffs_ecc_other *read_ecc,
+			    const struct yaffs_ecc_other *test_ecc);
+#endif
diff --git a/fs/yaffs2/yaffs_getblockinfo.h b/fs/yaffs2/yaffs_getblockinfo.h
new file mode 100644
index 0000000..d87acbd
--- /dev/null
+++ b/fs/yaffs2/yaffs_getblockinfo.h
@@ -0,0 +1,35 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+#ifndef __YAFFS_GETBLOCKINFO_H__
+#define __YAFFS_GETBLOCKINFO_H__
+
+#include "yaffs_guts.h"
+#include "yaffs_trace.h"
+
+/* Function to manipulate block info */
+static inline struct yaffs_block_info *yaffs_get_block_info(struct yaffs_dev
+							      *dev, int blk)
+{
+	if (blk < dev->internal_start_block || blk > dev->internal_end_block) {
+		yaffs_trace(YAFFS_TRACE_ERROR,
+			"**>> yaffs: get_block_info block %d is not valid",
+			blk);
+		YBUG();
+	}
+	return &dev->block_info[blk - dev->internal_start_block];
+}
+
+#endif
diff --git a/fs/yaffs2/yaffs_guts.c b/fs/yaffs2/yaffs_guts.c
new file mode 100644
index 0000000..f4ae9de
--- /dev/null
+++ b/fs/yaffs2/yaffs_guts.c
@@ -0,0 +1,5164 @@
+/*
+ * YAFFS: Yet Another Flash File System. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU General Public License version 2 as
+ * published by the Free Software Foundation.
+ */
+
+#include "yportenv.h"
+#include "yaffs_trace.h"
+
+#include "yaffs_guts.h"
+#include "yaffs_tagsvalidity.h"
+#include "yaffs_getblockinfo.h"
+
+#include "yaffs_tagscompat.h"
+
+#include "yaffs_nand.h"
+
+#include "yaffs_yaffs1.h"
+#include "yaffs_yaffs2.h"
+#include "yaffs_bitmap.h"
+#include "yaffs_verify.h"
+
+#include "yaffs_nand.h"
+#include "yaffs_packedtags2.h"
+
+#include "yaffs_nameval.h"
+#include "yaffs_allocator.h"
+
+#include "yaffs_attribs.h"
+
+/* Note YAFFS_GC_GOOD_ENOUGH must be <= YAFFS_GC_PASSIVE_THRESHOLD */
+#define YAFFS_GC_GOOD_ENOUGH 2
+#define YAFFS_GC_PASSIVE_THRESHOLD 4
+
+#include "yaffs_ecc.h"
+
+/* Forward declarations */
+
+static int yaffs_wr_data_obj(struct yaffs_obj *in, int inode_chunk,
+			     const u8 * buffer, int n_bytes, int use_reserve);
+
+
+
+/* Function to calculate chunk and offset */
+
+static void yaffs_addr_to_chunk(struct yaffs_dev *dev, loff_t addr,
+				int *chunk_out, u32 * offset_out)
+{
+	int chunk;
+	u32 offset;
+
+	chunk = (u32) (addr >> dev->chunk_shift);
+
+	if (dev->chunk_div == 1) {
+		/* easy power of 2 case */
+		offset = (u32) (addr & dev->chunk_mask);
+	} else {
+		/* Non power-of-2 case */
+
+		loff_t chunk_base;
+
+		chunk /= dev->chunk_div;
+
+		chunk_base = ((loff_t) chunk) * dev->data_bytes_per_chunk;
+		offset = (u32) (addr - chunk_base);
+	}
+
+	*chunk_out = chunk;
+	*offset_out = offset;
+}
+
+/* Function to return the number of shifts for a power of 2 greater than or
+ * equal to the given number
+ * Note we don't try to cater for all possible numbers and this does not have to
+ * be hellishly efficient.
+ */
+
+static u32 calc_shifts_ceiling(u32 x)
+{
+	int extra_bits;
+	int shifts;
+
+	shifts = extra_bits = 0;
+
+	while (x > 1) {
+		if (x & 1)
+			extra_bits++;
+		x >>= 1;
+		shifts++;
+	}
+
+	if (extra_bits)
+		shifts++;
+
+	return shifts;
+}
+
+/* Function to return the number of shifts to get a 1 in bit 0
+ */
+
+static u32 calc_shifts(u32 x)
+{
+	u32 shifts;
+
+	shifts = 0;
+
+	if (!x)
+		return 0;
+
+	while (!(x & 1)) {
+		x >>= 1;
+		shifts++;
+	}
+
+	return shifts;
+}
+
+/*
+ * Temporary buffer manipulations.
+ */
+
+static int yaffs_init_tmp_buffers(struct yaffs_dev *dev)
+{
+	int i;
+	u8 *buf = (u8 *) 1;
+
+	memset(dev->temp_buffer, 0, sizeof(dev->temp_buffer));
+
+	for (i = 0; buf && i < YAFFS_N_TEMP_BUFFERS; i++) {
+		dev->temp_buffer[i].line = 0;	/* not in use */
+		dev->temp_buffer[i].buffer = buf =
+		    kmalloc(dev->param.total_bytes_per_chunk, GFP_NOFS);
+	}
+
+	return buf ? YAFFS_OK : YAFFS_FAIL;
+}
+
+u8 *yaffs_get_temp_buffer(struct yaffs_dev * dev, int line_no)
+{
+	int i, j;
+
+	dev->temp_in_use++;
+	if (dev->temp_in_use > dev->max_temp)
+		dev->max_temp = dev->temp_in_use;
+
+	for (i = 0; i < YAFFS_N_TEMP_BUFFERS; i++) {
+		if (dev->temp_buffer[i].line == 0) {
+			dev->temp_buffer[i].line = line_no;
+			if ((i + 1) > dev->max_temp) {
+				dev->max_temp = i + 1;
+				for (j = 0; j <= i; j++)
+					dev->temp_buffer[j].max_line =
+					    dev->temp_buffer[j].line;
+			}
+
+			return dev->temp_buffer[i].buffer;
+		}
+	}
+
+	yaffs_trace(YAFFS_TRACE_BUFFERS,
+		"Out of temp buffers at line %d, other held by lines:",
+		line_no);
+	for (i = 0; i < YAFFS_N_TEMP_BUFFERS; i++)
+		yaffs_trace(YAFFS_TRACE_BUFFERS," %d", dev->temp_buffer[i].line);
+
+	/*
+	 * If we got here then we have to allocate an unmanaged one
+	 * This is not good.
+	 */
+
+	dev->unmanaged_buffer_allocs++;
+	return kmalloc(dev->data_bytes_per_chunk, GFP_NOFS);
+
+}
+
+void yaffs_release_temp_buffer(struct yaffs_dev *dev, u8 * buffer, int line_no)
+{
+	int i;
+
+	dev->temp_in_use--;
+
+	for (i = 0; i < YAFFS_N_TEMP_BUFFERS; i++) {
+		if (dev->temp_buffer[i].buffer == buffer) {
+			dev->temp_buffer[i].line = 0;
+			return;
+		}
+	}
+
+	if (buffer) {
+		/* assume it is an unmanaged one. */
+		yaffs_trace(YAFFS_TRACE_BUFFERS,
+		  "Releasing unmanaged temp buffer in line %d",
+		   line_no);
+		kfree(buffer);
+		dev->unmanaged_buffer_deallocs++;
+	}
+
+}
+
+/*
+ * Determine if we have a managed buffer.
+ */
+int yaffs_is_managed_tmp_buffer(struct yaffs_dev *dev, const u8 * buffer)
+{
+	int i;
+
+	for (i = 0; i < YAFFS_N_TEMP_BUFFERS; i++) {
+		if (dev->temp_buffer[i].buffer == buffer)
+			return 1;
+	}
+
+	for (i = 0; i < dev->param.n_caches; i++) {
+		if (dev->cache[i].data == buffer)
+			return 1;
+	}
+
+	if (buffer == dev->checkpt_buffer)
+		return 1;
+
+	yaffs_trace(YAFFS_TRACE_ALWAYS,
+	  "yaffs: unmaged buffer detected.");
+	return 0;
+}
+
+/*
+ * Functions for robustisizing TODO
+ *
+ */
+
+static void yaffs_handle_chunk_wr_ok(struct yaffs_dev *dev, int nand_chunk,
+				     const u8 * data,
+				     const struct yaffs_ext_tags *tags)
+{
+	dev = dev;
+	nand_chunk = nand_chunk;
+	data = data;
+	tags = tags;
+}
+
+static void yaffs_handle_chunk_update(struct yaffs_dev *dev, int nand_chunk,
+				      const struct yaffs_ext_tags *tags)
+{
+	dev = dev;
+	nand_chunk = nand_chunk;
+	tags = tags;
+}
+
+void yaffs_handle_chunk_error(struct yaffs_dev *dev,
+			      struct yaffs_block_info *bi)
+{
+	if (!bi->gc_prioritise) {
+		bi->gc_prioritise = 1;
+		dev->has_pending_prioritised_gc = 1;
+		bi->chunk_error_strikes++;
+
+		if (bi->chunk_error_strikes > 3) {
+			bi->needs_retiring = 1;	/* Too many stikes, so retire this */
+			yaffs_trace(YAFFS_TRACE_ALWAYS, "yaffs: Block struck out");
+
+		}
+	}
+}
+
+static void yaffs_handle_chunk_wr_error(struct yaffs_dev *dev, int nand_chunk,
+					int erased_ok)
+{
+	int flash_block = nand_chunk / dev->param.chunks_per_block;
+	struct yaffs_block_info *bi = yaffs_get_block_info(dev, flash_block);
+
+	yaffs_handle_chunk_error(dev, bi);
+
+	if (erased_ok) {
+		/* Was an actual write failure, so mark the block for retirement  */
+		bi->needs_retiring = 1;
+		yaffs_trace(YAFFS_TRACE_ERROR | YAFFS_TRACE_BAD_BLOCKS,
+		  "**>> Block %d needs retiring", flash_block);
+	}
+
+	/* Delete the chunk */
+	yaffs_chunk_del(dev, nand_chunk, 1, __LINE__);
+	yaffs_skip_rest_of_block(dev);
+}
+
+/*
+ * Verification code
+ */
+
+/*
+ *  Simple hash function. Needs to have a reasonable spread
+ */
+
+static inline int yaffs_hash_fn(int n)
+{
+	n = abs(n);
+	return n % YAFFS_NOBJECT_BUCKETS;
+}
+
+/*
+ * Access functions to useful fake objects.
+ * Note that root might have a presence in NAND if permissions are set.
+ */
+
+struct yaffs_obj *yaffs_root(struct yaffs_dev *dev)
+{
+	return dev->root_dir;
+}
+
+struct yaffs_obj *yaffs_lost_n_found(struct yaffs_dev *dev)
+{
+	return dev->lost_n_found;
+}
+
+/*
+ *  Erased NAND checking functions
+ */
+
+int yaffs_check_ff(u8 * buffer, int n_bytes)
+{
+	/* Horrible, slow implementation */
+	while (n_bytes--) {
+		if (*buffer != 0xFF)
+			return 0;
+		buffer++;
+	}
+	return 1;
+}
+
+static int yaffs_check_chunk_erased(struct yaffs_dev *dev, int nand_chunk)
+{
+	int retval = YAFFS_OK;
+	u8 *data = yaffs_get_temp_buffer(dev, __LINE__);
+	struct yaffs_ext_tags tags;
+	int result;
+
+	result = yaffs_rd_chunk_tags_nand(dev, nand_chunk, data, &tags);
+
+	if (tags.ecc_result > YAFFS_ECC_RESULT_NO_ERROR)
+		retval = YAFFS_FAIL;
+
+	if (!yaffs_check_ff(data, dev->data_bytes_per_chunk) ||
+		tags.chunk_used) {
+		yaffs_trace(YAFFS_TRACE_NANDACCESS, "Chunk %d not erased", nand_chunk);
+		retval = YAFFS_FAIL;
+	}
+
+	yaffs_release_temp_buffer(dev, data, __LINE__);
+
+	return retval;
+
+}
+
+static int yaffs_verify_chunk_written(struct yaffs_dev *dev,
+				      int nand_chunk,
+				      const u8 * data,
+				      struct yaffs_ext_tags *tags)
+{
+	int retval = YAFFS_OK;
+	struct yaffs_ext_tags temp_tags;
+	u8 *buffer = yaffs_get_temp_buffer(dev, __LINE__);
+	int result;
+
+	result = yaffs_rd_chunk_tags_nand(dev, nand_chunk, buffer, &temp_tags);
+	if (memcmp(buffer, data, dev->data_bytes_per_chunk) ||
+	    temp_tags.obj_id != tags->obj_id ||
+	    temp_tags.chunk_id != tags->chunk_id ||
+	    temp_tags.n_bytes != tags->n_bytes)
+		retval = YAFFS_FAIL;
+
+	yaffs_release_temp_buffer(dev, buffer, __LINE__);
+
+	return retval;
+}
+
+
+int yaffs_check_alloc_available(struct yaffs_dev *dev, int n_chunks)
+{
+	int reserved_chunks;
+	int reserved_blocks = dev->param.n_reserved_blocks;
+	int checkpt_blocks;
+
+	checkpt_blocks = yaffs_calc_checkpt_blocks_required(dev);
+
+	reserved_chunks =
+	    ((reserved_blocks + checkpt_blocks) * dev->param.chunks_per_block);
+
+	return (dev->n_free_chunks > (reserved_chunks + n_chunks));
+}
+
+static int yaffs_find_alloc_block(struct yaffs_dev *dev)
+{
+	int i;
+
+	struct yaffs_block_info *bi;
+
+	if (dev->n_erased_blocks < 1) {
+		/* Hoosterman we've got a problem.
+		 * Can't get space to gc
+		 */
+		yaffs_trace(YAFFS_TRACE_ERROR,
+		  "yaffs tragedy: no more erased blocks" );
+
+		return -1;
+	}
+
+	/* Find an empty block. */
+
+	for (i = dev->internal_start_block; i <= dev->internal_end_block; i++) {
+		dev->alloc_block_finder++;
+		if (dev->alloc_block_finder < dev->internal_start_block
+		    || dev->alloc_block_finder > dev->internal_end_block) {
+			dev->alloc_block_finder = dev->internal_start_block;
+		}
+
+		bi = yaffs_get_block_info(dev, dev->alloc_block_finder);
+
+		if (bi->block_state == YAFFS_BLOCK_STATE_EMPTY) {
+			bi->block_state = YAFFS_BLOCK_STATE_ALLOCATING;
+			dev->seq_number++;
+			bi->seq_number = dev->seq_number;
+			dev->n_erased_blocks--;
+			yaffs_trace(YAFFS_TRACE_ALLOCATE,
+			  "Allocated block %d, seq  %d, %d left" ,
+			   dev->alloc_block_finder, dev->seq_number,
+			   dev->n_erased_blocks);
+			return dev->alloc_block_finder;
+		}
+	}
+
+	yaffs_trace(YAFFS_TRACE_ALWAYS,
+		"yaffs tragedy: no more erased blocks, but there should have been %d",
+		dev->n_erased_blocks);
+
+	return -1;
+}
+
+static int yaffs_alloc_chunk(struct yaffs_dev *dev, int use_reserver,
+			     struct yaffs_block_info **block_ptr)
+{
+	int ret_val;
+	struct yaffs_block_info *bi;
+
+	if (dev->alloc_block < 0) {
+		/* Get next block to allocate off */
+		dev->alloc_block = yaffs_find_alloc_block(dev);
+		dev->alloc_page = 0;
+	}
+
+	if (!use_reserver && !yaffs_check_alloc_available(dev, 1)) {
+		/* Not enough space to allocate unless we're allowed to use the reserve. */
+		return -1;
+	}
+
+	if (dev->n_erased_blocks < dev->param.n_reserved_blocks
+	    && dev->alloc_page == 0)
+		yaffs_trace(YAFFS_TRACE_ALLOCATE, "Allocating reserve");
+
+	/* Next page please.... */
+	if (dev->alloc_block >= 0) {
+		bi = yaffs_get_block_info(dev, dev->alloc_block);
+
+		ret_val = (dev->alloc_block * dev->param.chunks_per_block) +
+		    dev->alloc_page;
+		bi->pages_in_use++;
+		yaffs_set_chunk_bit(dev, dev->alloc_block, dev->alloc_page);
+
+		dev->alloc_page++;
+
+		dev->n_free_chunks--;
+
+		/* If the block is full set the state to full */
+		if (dev->alloc_page >= dev->param.chunks_per_block) {
+			bi->block_state = YAFFS_BLOCK_STATE_FULL;
+			dev->alloc_block = -1;
+		}
+
+		if (block_ptr)
+			*block_ptr = bi;
+
+		return ret_val;
+	}
+
+	yaffs_trace(YAFFS_TRACE_ERROR, "!!!!!!!!! Allocator out !!!!!!!!!!!!!!!!!" );
+
+	return -1;
+}
+
+static int yaffs_get_erased_chunks(struct yaffs_dev *dev)
+{
+	int n;
+
+	n = dev->n_erased_blocks * dev->param.chunks_per_block;
+
+	if (dev->alloc_block > 0)
+		n += (dev->param.chunks_per_block - dev->alloc_page);
+
+	return n;
+
+}
+
+/*
+ * yaffs_skip_rest_of_block() skips over the rest of the allocation block
+ * if we don't want to write to it.
+ */
+void yaffs_skip_rest_of_block(struct yaffs_dev *dev)
+{
+	if (dev->alloc_block > 0) {
+		struct yaffs_block_info *bi =
+		    yaffs_get_block_info(dev, dev->alloc_block);
+		if (bi->block_state == YAFFS_BLOCK_STATE_ALLOCATING) {
+			bi->block_state = YAFFS_BLOCK_STATE_FULL;
+			dev->alloc_block = -1;
+		}
+	}
+}
+
+static int yaffs_write_new_chunk(struct yaffs_dev *dev,
+				 const u8 * data,
+				 struct yaffs_ext_tags *tags, int use_reserver)
+{
+	int attempts = 0;
+	int write_ok = 0;
+	int chunk;
+
+	yaffs2_checkpt_invalidate(dev);
+
+	do {
+		struct yaffs_block_info *bi = 0;
+		int erased_ok = 0;
+
+		chunk = yaffs_alloc_chunk(dev, use_reserver, &bi);
+		if (chunk < 0) {
+			/* no space */
+			break;
+		}
+
+		/* First check this chunk is erased, if it needs
+		 * checking.  The checking policy (unless forced
+		 * always on) is as follows:
+		 *
+		 * Check the first page we try to write in a block.
+		 * If the check passes then we don't need to check any
+		 * more.        If the check fails, we check again...
+		 * If the block has been erased, we don't need to check.
+		 *
+		 * However, if the block has been prioritised for gc,
+		 * then we think there might be something odd about
+		 * this block and stop using it.
+		 *
+		 * Rationale: We should only ever see chunks that have
+		 * not been erased if there was a partially written
+		 * chunk due to power loss.  This checking policy should
+		 * catch that case with very few checks and thus save a
+		 * lot of checks that are most likely not needed.
+		 *
+		 * Mods to the above
+		 * If an erase check fails or the write fails we skip the 
+		 * rest of the block.
+		 */
+
+		/* let's give it a try */
+		attempts++;
+
+		if (dev->param.always_check_erased)
+			bi->skip_erased_check = 0;
+
+		if (!bi->skip_erased_check) {
+			erased_ok = yaffs_check_chunk_erased(dev, chunk);
+			if (erased_ok != YAFFS_OK) {
+				yaffs_trace(YAFFS_TRACE_ERROR,
+				  "**>> yaffs chunk %d was not erased",
+				  chunk);
+
+				/* If not erased, delete this one,
+				 * skip rest of block and
+				 * try another chunk */
+				yaffs_chunk_del(dev, chunk, 1, __LINE__);
+				yaffs_skip_rest_of_block(dev);
+				continue;
+			}
+		}
+
+		write_ok = yaffs_wr_chunk_tags_nand(dev, chunk, data, tags);
+
+		if (!bi->skip_erased_check)
+			write_ok =
+			    yaffs_verify_chunk_written(dev, chunk, data, tags);
+
+		if (write_ok != YAFFS_OK) {
+			/* Clean up aborted write, skip to next block and
+			 * try another chunk */
+			yaffs_handle_chunk_wr_error(dev, chunk, erased_ok);
+			continue;
+		}
+
+		bi->skip_erased_check = 1;
+
+		/* Copy the data into the robustification buffer */
+		yaffs_handle_chunk_wr_ok(dev, chunk, data, tags);
+
+	} while (write_ok != YAFFS_OK &&
+		 (yaffs_wr_attempts <= 0 || attempts <= yaffs_wr_attempts));
+
+	if (!write_ok)
+		chunk = -1;
+
+	if (attempts > 1) {
+		yaffs_trace(YAFFS_TRACE_ERROR,
+			"**>> yaffs write required %d attempts",
+			attempts);
+		dev->n_retired_writes += (attempts - 1);
+	}
+
+	return chunk;
+}
+
+/*
+ * Block retiring for handling a broken block.
+ */
+
+static void yaffs_retire_block(struct yaffs_dev *dev, int flash_block)
+{
+	struct yaffs_block_info *bi = yaffs_get_block_info(dev, flash_block);
+
+	yaffs2_checkpt_invalidate(dev);
+
+	yaffs2_clear_oldest_dirty_seq(dev, bi);
+
+	if (yaffs_mark_bad(dev, flash_block) != YAFFS_OK) {
+		if (yaffs_erase_block(dev, flash_block) != YAFFS_OK) {
+			yaffs_trace(YAFFS_TRACE_ALWAYS,
+				"yaffs: Failed to mark bad and erase block %d",
+				flash_block);
+		} else {
+			struct yaffs_ext_tags tags;
+			int chunk_id =
+			    flash_block * dev->param.chunks_per_block;
+
+			u8 *buffer = yaffs_get_temp_buffer(dev, __LINE__);
+
+			memset(buffer, 0xff, dev->data_bytes_per_chunk);
+			yaffs_init_tags(&tags);
+			tags.seq_number = YAFFS_SEQUENCE_BAD_BLOCK;
+			if (dev->param.write_chunk_tags_fn(dev, chunk_id -
+							   dev->chunk_offset,
+							   buffer,
+							   &tags) != YAFFS_OK)
+				yaffs_trace(YAFFS_TRACE_ALWAYS,
+					"yaffs: Failed to write bad block marker to block %d",
+					flash_block);
+
+			yaffs_release_temp_buffer(dev, buffer, __LINE__);
+		}
+	}
+
+	bi->block_state = YAFFS_BLOCK_STATE_DEAD;
+	bi->gc_prioritise = 0;
+	bi->needs_retiring = 0;
+
+	dev->n_retired_blocks++;
+}
+
+/*---------------- Name handling functions ------------*/
+
+static u16 yaffs_calc_name_sum(const YCHAR * name)
+{
+	u16 sum = 0;
+	u16 i = 1;
+
+	const YUCHAR *bname = (const YUCHAR *)name;
+	if (bname) {
+		while ((*bname) && (i < (YAFFS_MAX_NAME_LENGTH / 2))) {
+
+			/* 0x1f mask is case insensitive */
+			sum += ((*bname) & 0x1f) * i;
+			i++;
+			bname++;
+		}
+	}
+	return sum;
+}
+
+void yaffs_set_obj_name(struct yaffs_obj *obj, const YCHAR * name)
+{
+#ifndef CONFIG_YAFFS_NO_SHORT_NAMES
+	memset(obj->short_name, 0, sizeof(obj->short_name));
+	if (name && 
+	        strnlen(name, YAFFS_SHORT_NAME_LENGTH + 1) <=
+	    YAFFS_SHORT_NAME_LENGTH)
+		strcpy(obj->short_name, name);
+	else
+		obj->short_name[0] = _Y('\0');
+#endif
+	obj->sum = yaffs_calc_name_sum(name);
+}
+
+void yaffs_set_obj_name_from_oh(struct yaffs_obj *obj,
+				const struct yaffs_obj_hdr *oh)
+{
+#ifdef CONFIG_YAFFS_AUTO_UNICODE
+	YCHAR tmp_name[YAFFS_MAX_NAME_LENGTH + 1];
+	memset(tmp_name, 0, sizeof(tmp_name));
+	yaffs_load_name_from_oh(obj->my_dev, tmp_name, oh->name,
+				YAFFS_MAX_NAME_LENGTH + 1);
+	yaffs_set_obj_name(obj, tmp_name);
+#else
+	yaffs_set_obj_name(obj, oh->name);
+#endif
+}
+
+/*-------------------- TNODES -------------------
+
+ * List of spare tnodes
+ * The list is hooked together using the first pointer
+ * in the tnode.
+ */
+
+struct yaffs_tnode *yaffs_get_tnode(struct yaffs_dev *dev)
+{
+	struct yaffs_tnode *tn = yaffs_alloc_raw_tnode(dev);
+	if (tn) {
+		memset(tn, 0, dev->tnode_size);
+		dev->n_tnodes++;
+	}
+
+	dev->checkpoint_blocks_required = 0;	/* force recalculation */
+
+	return tn;
+}
+
+/* FreeTnode frees up a tnode and puts it back on the free list */
+static void yaffs_free_tnode(struct yaffs_dev *dev, struct yaffs_tnode *tn)
+{
+	yaffs_free_raw_tnode(dev, tn);
+	dev->n_tnodes--;
+	dev->checkpoint_blocks_required = 0;	/* force recalculation */
+}
+
+static void yaffs_deinit_tnodes_and_objs(struct yaffs_dev *dev)
+{
+	yaffs_deinit_raw_tnodes_and_objs(dev);
+	dev->n_obj = 0;
+	dev->n_tnodes = 0;
+}
+
+void yaffs_load_tnode_0(struct yaffs_dev *dev, struct yaffs_tnode *tn,
+			unsigned pos, unsigned val)
+{
+	u32 *map = (u32 *) tn;
+	u32 bit_in_map;
+	u32 bit_in_word;
+	u32 word_in_map;
+	u32 mask;
+
+	pos &= YAFFS_TNODES_LEVEL0_MASK;
+	val >>= dev->chunk_grp_bits;
+
+	bit_in_map = pos * dev->tnode_width;
+	word_in_map = bit_in_map / 32;
+	bit_in_word = bit_in_map & (32 - 1);
+
+	mask = dev->tnode_mask << bit_in_word;
+
+	map[word_in_map] &= ~mask;
+	map[word_in_map] |= (mask & (val << bit_in_word));
+
+	if (dev->tnode_width > (32 - bit_in_word)) {
+		bit_in_word = (32 - bit_in_word);
+		word_in_map++;;
+		mask =
+		    dev->tnode_mask >> ( /*dev->tnode_width - */ bit_in_word);
+		map[word_in_map] &= ~mask;
+		map[word_in_map] |= (mask & (val >> bit_in_word));
+	}
+}
+
+u32 yaffs_get_group_base(struct yaffs_dev *dev, struct yaffs_tnode *tn,
+			 unsigned pos)
+{
+	u32 *map = (u32 *) tn;
+	u32 bit_in_map;
+	u32 bit_in_word;
+	u32 word_in_map;
+	u32 val;
+
+	pos &= YAFFS_TNODES_LEVEL0_MASK;
+
+	bit_in_map = pos * dev->tnode_width;
+	word_in_map = bit_in_map / 32;
+	bit_in_word = bit_in_map & (32 - 1);
+
+	val = map[word_in_map] >> bit_in_word;
+
+	if (dev->tnode_width > (32 - bit_in_word)) {
+		bit_in_word = (32 - bit_in_word);
+		word_in_map++;;
+		val |= (map[word_in_map] << bit_in_word);
+	}
+
+	val &= dev->tnode_mask;
+	val <<= dev->chunk_grp_bits;
+
+	return val;
+}
+
+/* ------------------- End of individual tnode manipulation -----------------*/
+
+/* ---------Functions to manipulate the look-up tree (made up of tnodes) ------
+ * The look up tree is represented by the top tnode and the number of top_level
+ * in the tree. 0 means only the level 0 tnode is in the tree.
+ */
+
+/* FindLevel0Tnode finds the level 0 tnode, if one exists. */
+struct yaffs_tnode *yaffs_find_tnode_0(struct yaffs_dev *dev,
+				       struct yaffs_file_var *file_struct,
+				       u32 chunk_id)
+{
+	struct yaffs_tnode *tn = file_struct->top;
+	u32 i;
+	int required_depth;
+	int level = file_struct->top_level;
+
+	dev = dev;
+
+	/* Check sane level and chunk Id */
+	if (level < 0 || level > YAFFS_TNODES_MAX_LEVEL)
+		return NULL;
+
+	if (chunk_id > YAFFS_MAX_CHUNK_ID)
+		return NULL;
+
+	/* First check we're tall enough (ie enough top_level) */
+
+	i = chunk_id >> YAFFS_TNODES_LEVEL0_BITS;
+	required_depth = 0;
+	while (i) {
+		i >>= YAFFS_TNODES_INTERNAL_BITS;
+		required_depth++;
+	}
+
+	if (required_depth > file_struct->top_level)
+		return NULL;	/* Not tall enough, so we can't find it */
+
+	/* Traverse down to level 0 */
+	while (level > 0 && tn) {
+		tn = tn->internal[(chunk_id >>
+				   (YAFFS_TNODES_LEVEL0_BITS +
+				    (level - 1) *
+				    YAFFS_TNODES_INTERNAL_BITS)) &
+				  YAFFS_TNODES_INTERNAL_MASK];
+		level--;
+	}
+
+	return tn;
+}
+
+/* AddOrFindLevel0Tnode finds the level 0 tnode if it exists, otherwise first expands the tree.
+ * This happens in two steps:
+ *  1. If the tree isn't tall enough, then make it taller.
+ *  2. Scan down the tree towards the level 0 tnode adding tnodes if required.
+ *
+ * Used when modifying the tree.
+ *
+ *  If the tn argument is NULL, then a fresh tnode will be added otherwise the specified tn will
+ *  be plugged into the ttree.
+ */
+
+struct yaffs_tnode *yaffs_add_find_tnode_0(struct yaffs_dev *dev,
+					   struct yaffs_file_var *file_struct,
+					   u32 chunk_id,
+					   struct yaffs_tnode *passed_tn)
+{
+	int required_depth;
+	int i;
+	int l;
+	struct yaffs_tnode *tn;
+
+	u32 x;
+
+	/* Check sane level and page Id */
+	if (file_struct->top_level < 0
+	    || file_struct->top_level > YAFFS_TNODES_MAX_LEVEL)
+		return NULL;
+
+	if (chunk_id > YAFFS_MAX_CHUNK_ID)
+		return NULL;
+
+	/* First check we're tall enough (ie enough top_level) */
+
+	x = chunk_id >> YAFFS_TNODES_LEVEL0_BITS;
+	required_depth = 0;
+	while (x) {
+		x >>= YAFFS_TNODES_INTERNAL_BITS;
+		required_depth++;
+	}
+
+	if (required_depth > file_struct->top_level) {
+		/* Not tall enough, gotta make the tree taller */
+		for (i = file_struct->top_level; i < required_depth; i++) {
+
+			tn = yaffs_get_tnode(dev);
+
+			if (tn) {
+				tn->internal[0] = file_struct->top;
+				file_struct->top = tn;
+				file_struct->top_level++;
+			} else {
+				yaffs_trace(YAFFS_TRACE_ERROR, "yaffs: no more tnodes");
+				return NULL;
+			}
+		}
+	}
+
+	/* Traverse down to level 0, adding anything we need */
+
+	l = file_struct->top_level;
+	tn = file_struct->top;
+
+	if (l > 0) {
+		while (l > 0 && tn) {
+			x = (chunk_id >>
+			     (YAFFS_TNODES_LEVEL0_BITS +
+			      (l - 1) * YAFFS_TNODES_INTERNAL_BITS)) &
+			    YAFFS_TNODES_INTERNAL_MASK;
+
+			if ((l > 1) && !tn->internal[x]) {
+				/* Add missing non-level-zero tnode */
+				tn->internal[x] = yaffs_get_tnode(dev);
+				if (!tn->internal[x])
+					return NULL;
+			} else if (l == 1) {
+				/* Looking from level 1 at level 0 */
+				if (passed_tn) {
+					/* If we already have one, then release it. */
+					if (tn->internal[x])
+						yaffs_free_tnode(dev,
+								 tn->
+								 internal[x]);
+					tn->internal[x] = passed_tn;
+
+				} else if (!tn->internal[x]) {
+					/* Don't have one, none passed in */
+					tn->internal[x] = yaffs_get_tnode(dev);
+					if (!tn->internal[x])
+						return NULL;
+				}
+			}
+
+			tn = tn->internal[x];
+			l--;
+		}
+	} else {
+		/* top is level 0 */
+		if (passed_tn) {
+			memcpy(tn, passed_tn,
+			       (dev->tnode_width * YAFFS_NTNODES_LEVEL0) / 8);
+			yaffs_free_tnode(dev, passed_tn);
+		}
+	}
+
+	return tn;
+}
+
+static int yaffs_tags_match(const struct yaffs_ext_tags *tags, int obj_id,
+			    int chunk_obj)
+{
+	return (tags->chunk_id == chunk_obj &&
+		tags->obj_id == obj_id && !tags->is_deleted) ? 1 : 0;
+
+}
+
+static int yaffs_find_chunk_in_group(struct yaffs_dev *dev, int the_chunk,
+				     struct yaffs_ext_tags *tags, int obj_id,
+				     int inode_chunk)
+{
+	int j;
+
+	for (j = 0; the_chunk && j < dev->chunk_grp_size; j++) {
+		if (yaffs_check_chunk_bit
+		    (dev, the_chunk / dev->param.chunks_per_block,
+		     the_chunk % dev->param.chunks_per_block)) {
+
+			if (dev->chunk_grp_size == 1)
+				return the_chunk;
+			else {
+				yaffs_rd_chunk_tags_nand(dev, the_chunk, NULL,
+							 tags);
+				if (yaffs_tags_match(tags, obj_id, inode_chunk)) {
+					/* found it; */
+					return the_chunk;
+				}
+			}
+		}
+		the_chunk++;
+	}
+	return -1;
+}
+
+static int yaffs_find_chunk_in_file(struct yaffs_obj *in, int inode_chunk,
+				    struct yaffs_ext_tags *tags)
+{
+	/*Get the Tnode, then get the level 0 offset chunk offset */
+	struct yaffs_tnode *tn;
+	int the_chunk = -1;
+	struct yaffs_ext_tags local_tags;
+	int ret_val = -1;
+
+	struct yaffs_dev *dev = in->my_dev;
+
+	if (!tags) {
+		/* Passed a NULL, so use our own tags space */
+		tags = &local_tags;
+	}
+
+	tn = yaffs_find_tnode_0(dev, &in->variant.file_variant, inode_chunk);
+
+	if (tn) {
+		the_chunk = yaffs_get_group_base(dev, tn, inode_chunk);
+
+		ret_val =
+		    yaffs_find_chunk_in_group(dev, the_chunk, tags, in->obj_id,
+					      inode_chunk);
+	}
+	return ret_val;
+}
+
+static int yaffs_find_del_file_chunk(struct yaffs_obj *in, int inode_chunk,
+				     struct yaffs_ext_tags *tags)
+{
+	/* Get the Tnode, then get the level 0 offset chunk offset */
+	struct yaffs_tnode *tn;
+	int the_chunk = -1;
+	struct yaffs_ext_tags local_tags;
+
+	struct yaffs_dev *dev = in->my_dev;
+	int ret_val = -1;
+
+	if (!tags) {
+		/* Passed a NULL, so use our own tags space */
+		tags = &local_tags;
+	}
+
+	tn = yaffs_find_tnode_0(dev, &in->variant.file_variant, inode_chunk);
+
+	if (tn) {
+
+		the_chunk = yaffs_get_group_base(dev, tn, inode_chunk);
+
+		ret_val =
+		    yaffs_find_chunk_in_group(dev, the_chunk, tags, in->obj_id,
+					      inode_chunk);
+
+		/* Delete the entry in the filestructure (if found) */
+		if (ret_val != -1)
+			yaffs_load_tnode_0(dev, tn, inode_chunk, 0);
+	}
+
+	return ret_val;
+}
+
+int yaffs_put_chunk_in_file(struct yaffs_obj *in, int inode_chunk,
+			    int nand_chunk, int in_scan)
+{
+	/* NB in_scan is zero unless scanning.
+	 * For forward scanning, in_scan is > 0;
+	 * for backward scanning in_scan is < 0
+	 *
+	 * nand_chunk = 0 is a dummy insert to make sure the tnodes are there.
+	 */
+
+	struct yaffs_tnode *tn;
+	struct yaffs_dev *dev = in->my_dev;
+	int existing_cunk;
+	struct yaffs_ext_tags existing_tags;
+	struct yaffs_ext_tags new_tags;
+	unsigned existing_serial, new_serial;
+
+	if (in->variant_type != YAFFS_OBJECT_TYPE_FILE) {
+		/* Just ignore an attempt at putting a chunk into a non-file during scanning
+		 * If it is not during Scanning then something went wrong!
+		 */
+		if (!in_scan) {
+			yaffs_trace(YAFFS_TRACE_ERROR,
+				"yaffs tragedy:attempt to put data chunk into a non-file"
+				);
+			YBUG();
+		}
+
+		yaffs_chunk_del(dev, nand_chunk, 1, __LINE__);
+		return YAFFS_OK;
+	}
+
+	tn = yaffs_add_find_tnode_0(dev,
+				    &in->variant.file_variant,
+				    inode_chunk, NULL);
+	if (!tn)
+		return YAFFS_FAIL;
+
+	if (!nand_chunk)
+		/* Dummy insert, bail now */
+		return YAFFS_OK;
+
+	existing_cunk = yaffs_get_group_base(dev, tn, inode_chunk);
+
+	if (in_scan != 0) {
+		/* If we're scanning then we need to test for duplicates
+		 * NB This does not need to be efficient since it should only ever
+		 * happen when the power fails during a write, then only one
+		 * chunk should ever be affected.
+		 *
+		 * Correction for YAFFS2: This could happen quite a lot and we need to think about efficiency! TODO
+		 * Update: For backward scanning we don't need to re-read tags so this is quite cheap.
+		 */
+
+		if (existing_cunk > 0) {
+			/* NB Right now existing chunk will not be real chunk_id if the chunk group size > 1
+			 *    thus we have to do a FindChunkInFile to get the real chunk id.
+			 *
+			 * We have a duplicate now we need to decide which one to use:
+			 *
+			 * Backwards scanning YAFFS2: The old one is what we use, dump the new one.
+			 * Forward scanning YAFFS2: The new one is what we use, dump the old one.
+			 * YAFFS1: Get both sets of tags and compare serial numbers.
+			 */
+
+			if (in_scan > 0) {
+				/* Only do this for forward scanning */
+				yaffs_rd_chunk_tags_nand(dev,
+							 nand_chunk,
+							 NULL, &new_tags);
+
+				/* Do a proper find */
+				existing_cunk =
+				    yaffs_find_chunk_in_file(in, inode_chunk,
+							     &existing_tags);
+			}
+
+			if (existing_cunk <= 0) {
+				/*Hoosterman - how did this happen? */
+
+				yaffs_trace(YAFFS_TRACE_ERROR,
+					"yaffs tragedy: existing chunk < 0 in scan"
+					);
+
+			}
+
+			/* NB The deleted flags should be false, otherwise the chunks will
+			 * not be loaded during a scan
+			 */
+
+			if (in_scan > 0) {
+				new_serial = new_tags.serial_number;
+				existing_serial = existing_tags.serial_number;
+			}
+
+			if ((in_scan > 0) &&
+			    (existing_cunk <= 0 ||
+			     ((existing_serial + 1) & 3) == new_serial)) {
+				/* Forward scanning.
+				 * Use new
+				 * Delete the old one and drop through to update the tnode
+				 */
+				yaffs_chunk_del(dev, existing_cunk, 1,
+						__LINE__);
+			} else {
+				/* Backward scanning or we want to use the existing one
+				 * Use existing.
+				 * Delete the new one and return early so that the tnode isn't changed
+				 */
+				yaffs_chunk_del(dev, nand_chunk, 1, __LINE__);
+				return YAFFS_OK;
+			}
+		}
+
+	}
+
+	if (existing_cunk == 0)
+		in->n_data_chunks++;
+
+	yaffs_load_tnode_0(dev, tn, inode_chunk, nand_chunk);
+
+	return YAFFS_OK;
+}
+
+static void yaffs_soft_del_chunk(struct yaffs_dev *dev, int chunk)
+{
+	struct yaffs_block_info *the_block;
+	unsigned block_no;
+
+	yaffs_trace(YAFFS_TRACE_DELETION, "soft delete chunk %d", chunk);
+
+	block_no = chunk / dev->param.chunks_per_block;
+	the_block = yaffs_get_block_info(dev, block_no);
+	if (the_block) {
+		the_block->soft_del_pages++;
+		dev->n_free_chunks++;
+		yaffs2_update_oldest_dirty_seq(dev, block_no, the_block);
+	}
+}
+
+/* SoftDeleteWorker scans backwards through the tnode tree and soft deletes all the chunks in the file.
+ * All soft deleting does is increment the block's softdelete count and pulls the chunk out
+ * of the tnode.
+ * Thus, essentially this is the same as DeleteWorker except that the chunks are soft deleted.
+ */
+
+static int yaffs_soft_del_worker(struct yaffs_obj *in, struct yaffs_tnode *tn,
+				 u32 level, int chunk_offset)
+{
+	int i;
+	int the_chunk;
+	int all_done = 1;
+	struct yaffs_dev *dev = in->my_dev;
+
+	if (tn) {
+		if (level > 0) {
+
+			for (i = YAFFS_NTNODES_INTERNAL - 1; all_done && i >= 0;
+			     i--) {
+				if (tn->internal[i]) {
+					all_done =
+					    yaffs_soft_del_worker(in,
+								  tn->internal
+								  [i],
+								  level - 1,
+								  (chunk_offset
+								   <<
+								   YAFFS_TNODES_INTERNAL_BITS)
+								  + i);
+					if (all_done) {
+						yaffs_free_tnode(dev,
+								 tn->internal
+								 [i]);
+						tn->internal[i] = NULL;
+					} else {
+						/* Hoosterman... how could this happen? */
+					}
+				}
+			}
+			return (all_done) ? 1 : 0;
+		} else if (level == 0) {
+
+			for (i = YAFFS_NTNODES_LEVEL0 - 1; i >= 0; i--) {
+				the_chunk = yaffs_get_group_base(dev, tn, i);
+				if (the_chunk) {
+					/* Note this does not find the real chunk, only the chunk group.
+					 * We make an assumption that a chunk group is not larger than
+					 * a block.
+					 */
+					yaffs_soft_del_chunk(dev, the_chunk);
+					yaffs_load_tnode_0(dev, tn, i, 0);
+				}
+
+			}
+			return 1;
+
+		}
+
+	}
+
+	return 1;
+
+}
+
+static void yaffs_remove_obj_from_dir(struct yaffs_obj *obj)
+{
+	struct yaffs_dev *dev = obj->my_dev;
+	struct yaffs_obj *parent;
+
+	yaffs_verify_obj_in_dir(obj);
+	parent = obj->parent;
+
+	yaffs_verify_dir(parent);
+
+	if (dev && dev->param.remove_obj_fn)
+		dev->param.remove_obj_fn(obj);
+
+	list_del_init(&obj->siblings);
+	obj->parent = NULL;
+
+	yaffs_verify_dir(parent);
+}
+
+void yaffs_add_obj_to_dir(struct yaffs_obj *directory, struct yaffs_obj *obj)
+{
+	if (!directory) {
+		yaffs_trace(YAFFS_TRACE_ALWAYS,
+			"tragedy: Trying to add an object to a null pointer directory"
+			);
+		YBUG();
+		return;
+	}
+	if (directory->variant_type != YAFFS_OBJECT_TYPE_DIRECTORY) {
+		yaffs_trace(YAFFS_TRACE_ALWAYS,
+			"tragedy: Trying to add an object to a non-directory"
+			);
+		YBUG();
+	}
+
+	if (obj->siblings.prev == NULL) {
+		/* Not initialised */
+		YBUG();
+	}
+
+	yaffs_verify_dir(directory);
+
+	yaffs_remove_obj_from_dir(obj);
+
+	/* Now add it */
+	list_add(&obj->siblings, &directory->variant.dir_variant.children);
+	obj->parent = directory;
+
+	if (directory == obj->my_dev->unlinked_dir
+	    || directory == obj->my_dev->del_dir) {
+		obj->unlinked = 1;
+		obj->my_dev->n_unlinked_files++;
+		obj->rename_allowed = 0;
+	}
+
+	yaffs_verify_dir(directory);
+	yaffs_verify_obj_in_dir(obj);
+}
+
+static int yaffs_change_obj_name(struct yaffs_obj *obj,
+				 struct yaffs_obj *new_dir,
+				 const YCHAR * new_name, int force, int shadows)
+{
+	int unlink_op;
+	int del_op;
+
+	struct yaffs_obj *existing_target;
+
+	if (new_dir == NULL)
+		new_dir = obj->parent;	/* use the old directory */
+
+	if (new_dir->variant_type != YAFFS_OBJECT_TYPE_DIRECTORY) {
+		yaffs_trace(YAFFS_TRACE_ALWAYS,
+			"tragedy: yaffs_change_obj_name: new_dir is not a directory"
+			);
+		YBUG();
+	}
+
+	/* TODO: Do we need this different handling for YAFFS2 and YAFFS1?? */
+	if (obj->my_dev->param.is_yaffs2)
+		unlink_op = (new_dir == obj->my_dev->unlinked_dir);
+	else
+		unlink_op = (new_dir == obj->my_dev->unlinked_dir
+			     && obj->variant_type == YAFFS_OBJECT_TYPE_FILE);
+
+	del_op = (new_dir == obj->my_dev->del_dir);
+
+	existing_target = yaffs_find_by_name(new_dir, new_name);
+
+	/* If the object is a file going into the unlinked directory,
+	 *   then it is OK to just stuff it in since duplicate names are allowed.
+	 *   else only proceed if the new name does not exist and if we're putting
+	 *   it into a directory.
+	 */
+	if ((unlink_op ||
+	     del_op ||
+	     force ||
+	     (shadows > 0) ||
+	     !existing_target) &&
+	    new_dir->variant_type == YAFFS_OBJECT_TYPE_DIRECTORY) {
+		yaffs_set_obj_name(obj, new_name);
+		obj->dirty = 1;
+
+		yaffs_add_obj_to_dir(new_dir, obj);
+
+		if (unlink_op)
+			obj->unlinked = 1;
+
+		/* If it is a deletion then we mark it as a shrink for gc purposes. */
+		if (yaffs_update_oh(obj, new_name, 0, del_op, shadows, NULL) >=
+		    0)
+			return YAFFS_OK;
+	}
+
+	return YAFFS_FAIL;
+}
+
+/*------------------------ Short Operations Cache ----------------------------------------
+ *   In many situations where there is no high level buffering  a lot of
+ *   reads might be short sequential reads, and a lot of writes may be short
+ *   sequential writes. eg. scanning/writing a jpeg file.
+ *   In these cases, a short read/write cache can provide a huge perfomance
+ *   benefit with dumb-as-a-rock code.
+ *   In Linux, the page cache provides read buffering and the short op cache 
+ *   provides write buffering.
+ *
+ *   There are a limited number (~10) of cache chunks per device so that we don't
+ *   need a very intelligent search.
+ */
+
+static int yaffs_obj_cache_dirty(struct yaffs_obj *obj)
+{
+	struct yaffs_dev *dev = obj->my_dev;
+	int i;
+	struct yaffs_cache *cache;
+	int n_caches = obj->my_dev->param.n_caches;
+
+	for (i = 0; i < n_caches; i++) {
+		cache = &dev->cache[i];
+		if (cache->object == obj && cache->dirty)
+			return 1;
+	}
+
+	return 0;
+}
+
+static void yaffs_flush_file_cache(struct yaffs_obj *obj)
+{
+	struct yaffs_dev *dev = obj->my_dev;
+	int lowest = -99;	/* Stop compiler whining. */
+	int i;
+	struct yaffs_cache *cache;
+	int chunk_written = 0;
+	int n_caches = obj->my_dev->param.n_caches;
+
+	if (n_caches > 0) {
+		do {
+			cache = NULL;
+
+			/* Find the dirty cache for this object with the lowest chunk id. */
+			for (i = 0; i < n_caches; i++) {
+				if (dev->cache[i].object == obj &&
+				    dev->cache[i].dirty) {
+					if (!cache
+					    || dev->cache[i].chunk_id <
+					    lowest) {
+						cache = &dev->cache[i];
+						lowest = cache->chunk_id;
+					}
+				}
+			}
+
+			if (cache && !cache->locked) {
+				/* Write it out and free it up */
+
+				chunk_written =
+				    yaffs_wr_data_obj(cache->object,
+						      cache->chunk_id,
+						      cache->data,
+						      cache->n_bytes, 1);
+				cache->dirty = 0;
+				cache->object = NULL;
+			}
+
+		} while (cache && chunk_written > 0);
+
+		if (cache)
+			/* Hoosterman, disk full while writing cache out. */
+			yaffs_trace(YAFFS_TRACE_ERROR,
+				"yaffs tragedy: no space during cache write");
+
+	}
+
+}
+
+/*yaffs_flush_whole_cache(dev)
+ *
+ *
+ */
+
+void yaffs_flush_whole_cache(struct yaffs_dev *dev)
+{
+	struct yaffs_obj *obj;
+	int n_caches = dev->param.n_caches;
+	int i;
+
+	/* Find a dirty object in the cache and flush it...
+	 * until there are no further dirty objects.
+	 */
+	do {
+		obj = NULL;
+		for (i = 0; i < n_caches && !obj; i++) {
+			if (dev->cache[i].object && dev->cache[i].dirty)
+				obj = dev->cache[i].object;
+
+		}
+		if (obj)
+			yaffs_flush_file_cache(obj);
+
+	} while (obj);
+
+}
+
+/* Grab us a cache chunk for use.
+ * First look for an empty one.
+ * Then look for the least recently used non-dirty one.
+ * Then look for the least recently used dirty one...., flush and look again.
+ */
+static struct yaffs_cache *yaffs_grab_chunk_worker(struct yaffs_dev *dev)
+{
+	int i;
+
+	if (dev->param.n_caches > 0) {
+		for (i = 0; i < dev->param.n_caches; i++) {
+			if (!dev->cache[i].object)
+				return &dev->cache[i];
+		}
+	}
+
+	return NULL;
+}
+
+static struct yaffs_cache *yaffs_grab_chunk_cache(struct yaffs_dev *dev)
+{
+	struct yaffs_cache *cache;
+	struct yaffs_obj *the_obj;
+	int usage;
+	int i;
+	int pushout;
+
+	if (dev->param.n_caches > 0) {
+		/* Try find a non-dirty one... */
+
+		cache = yaffs_grab_chunk_worker(dev);
+
+		if (!cache) {
+			/* They were all dirty, find the last recently used object and flush
+			 * its cache, then  find again.
+			 * NB what's here is not very accurate, we actually flush the object
+			 * the last recently used page.
+			 */
+
+			/* With locking we can't assume we can use entry zero */
+
+			the_obj = NULL;
+			usage = -1;
+			cache = NULL;
+			pushout = -1;
+
+			for (i = 0; i < dev->param.n_caches; i++) {
+				if (dev->cache[i].object &&
+				    !dev->cache[i].locked &&
+				    (dev->cache[i].last_use < usage
+				     || !cache)) {
+					usage = dev->cache[i].last_use;
+					the_obj = dev->cache[i].object;
+					cache = &dev->cache[i];
+					pushout = i;
+				}
+			}
+
+			if (!cache || cache->dirty) {
+				/* Flush and try again */
+				yaffs_flush_file_cache(the_obj);
+				cache = yaffs_grab_chunk_worker(dev);
+			}
+
+		}
+		return cache;
+	} else {
+		return NULL;
+        }
+}
+
+/* Find a cached chunk */
+static struct yaffs_cache *yaffs_find_chunk_cache(const struct yaffs_obj *obj,
+						  int chunk_id)
+{
+	struct yaffs_dev *dev = obj->my_dev;
+	int i;
+	if (dev->param.n_caches > 0) {
+		for (i = 0; i < dev->param.n_caches; i++) {
+			if (dev->cache[i].object == obj &&
+			    dev->cache[i].chunk_id == chunk_id) {
+				dev->cache_hits++;
+
+				return &dev->cache[i];
+			}
+		}
+	}
+	return NULL;
+}
+
+/* Mark the chunk for the least recently used algorithym */
+static void yaffs_use_cache(struct yaffs_dev *dev, struct yaffs_cache *cache,
+			    int is_write)
+{
+
+	if (dev->param.n_caches > 0) {
+		if (dev->cache_last_use < 0 || dev->cache_last_use > 100000000) {
+			/* Reset the cache usages */
+			int i;
+			for (i = 1; i < dev->param.n_caches; i++)
+				dev->cache[i].last_use = 0;
+
+			dev->cache_last_use = 0;
+		}
+
+		dev->cache_last_use++;
+
+		cache->last_use = dev->cache_last_use;
+
+		if (is_write)
+			cache->dirty = 1;
+	}
+}
+
+/* Invalidate a single cache page.
+ * Do this when a whole page gets written,
+ * ie the short cache for this page is no longer valid.
+ */
+static void yaffs_invalidate_chunk_cache(struct yaffs_obj *object, int chunk_id)
+{
+	if (object->my_dev->param.n_caches > 0) {
+		struct yaffs_cache *cache =
+		    yaffs_find_chunk_cache(object, chunk_id);
+
+		if (cache)
+			cache->object = NULL;
+	}
+}
+
+/* Invalidate all the cache pages associated with this object
+ * Do this whenever ther file is deleted or resized.
+ */
+static void yaffs_invalidate_whole_cache(struct yaffs_obj *in)
+{
+	int i;
+	struct yaffs_dev *dev = in->my_dev;
+
+	if (dev->param.n_caches > 0) {
+		/* Invalidate it. */
+		for (i = 0; i < dev->param.n_caches; i++) {
+			if (dev->cache[i].object == in)
+				dev->cache[i].object = NULL;
+		}
+	}
+}
+
+static void yaffs_unhash_obj(struct yaffs_obj *obj)
+{
+	int bucket;
+	struct yaffs_dev *dev = obj->my_dev;
+
+	/* If it is still linked into the bucket list, free from the list */
+	if (!list_empty(&obj->hash_link)) {
+		list_del_init(&obj->hash_link);
+		bucket = yaffs_hash_fn(obj->obj_id);
+		dev->obj_bucket[bucket].count--;
+	}
+}
+
+/*  FreeObject frees up a Object and puts it back on the free list */
+static void yaffs_free_obj(struct yaffs_obj *obj)
+{
+	struct yaffs_dev *dev = obj->my_dev;
+
+	yaffs_trace(YAFFS_TRACE_OS, "FreeObject %p inode %p",
+		obj, obj->my_inode);
+
+	if (!obj)
+		YBUG();
+	if (obj->parent)
+		YBUG();
+	if (!list_empty(&obj->siblings))
+		YBUG();
+
+	if (obj->my_inode) {
+		/* We're still hooked up to a cached inode.
+		 * Don't delete now, but mark for later deletion
+		 */
+		obj->defered_free = 1;
+		return;
+	}
+
+	yaffs_unhash_obj(obj);
+
+	yaffs_free_raw_obj(dev, obj);
+	dev->n_obj--;
+	dev->checkpoint_blocks_required = 0;	/* force recalculation */
+}
+
+void yaffs_handle_defered_free(struct yaffs_obj *obj)
+{
+	if (obj->defered_free)
+		yaffs_free_obj(obj);
+}
+
+static int yaffs_generic_obj_del(struct yaffs_obj *in)
+{
+
+	/* First off, invalidate the file's data in the cache, without flushing. */
+	yaffs_invalidate_whole_cache(in);
+
+	if (in->my_dev->param.is_yaffs2 && (in->parent != in->my_dev->del_dir)) {
+		/* Move to the unlinked directory so we have a record that it was deleted. */
+		yaffs_change_obj_name(in, in->my_dev->del_dir, _Y("deleted"), 0,
+				      0);
+
+	}
+
+	yaffs_remove_obj_from_dir(in);
+	yaffs_chunk_del(in->my_dev, in->hdr_chunk, 1, __LINE__);
+	in->hdr_chunk = 0;
+
+	yaffs_free_obj(in);
+	return YAFFS_OK;
+
+}
+
+static void yaffs_soft_del_file(struct yaffs_obj *obj)
+{
+	if (obj->deleted &&
+	    obj->variant_type == YAFFS_OBJECT_TYPE_FILE && !obj->soft_del) {
+		if (obj->n_data_chunks <= 0) {
+			/* Empty file with no duplicate object headers,
+			 * just delete it immediately */
+			yaffs_free_tnode(obj->my_dev,
+					 obj->variant.file_variant.top);
+			obj->variant.file_variant.top = NULL;
+			yaffs_trace(YAFFS_TRACE_TRACING,
+				"yaffs: Deleting empty file %d",
+				obj->obj_id);
+			yaffs_generic_obj_del(obj);
+		} else {
+			yaffs_soft_del_worker(obj,
+					      obj->variant.file_variant.top,
+					      obj->variant.
+					      file_variant.top_level, 0);
+			obj->soft_del = 1;
+		}
+	}
+}
+
+/* Pruning removes any part of the file structure tree that is beyond the
+ * bounds of the file (ie that does not point to chunks).
+ *
+ * A file should only get pruned when its size is reduced.
+ *
+ * Before pruning, the chunks must be pulled from the tree and the
+ * level 0 tnode entries must be zeroed out.
+ * Could also use this for file deletion, but that's probably better handled
+ * by a special case.
+ *
+ * This function is recursive. For levels > 0 the function is called again on
+ * any sub-tree. For level == 0 we just check if the sub-tree has data.
+ * If there is no data in a subtree then it is pruned.
+ */
+
+static struct yaffs_tnode *yaffs_prune_worker(struct yaffs_dev *dev,
+					      struct yaffs_tnode *tn, u32 level,
+					      int del0)
+{
+	int i;
+	int has_data;
+
+	if (tn) {
+		has_data = 0;
+
+		if (level > 0) {
+			for (i = 0; i < YAFFS_NTNODES_INTERNAL; i++) {
+				if (tn->internal[i]) {
+					tn->internal[i] =
+					    yaffs_prune_worker(dev,
+							       tn->internal[i],
+							       level - 1,
+							       (i ==
+								0) ? del0 : 1);
+				}
+
+				if (tn->internal[i])
+					has_data++;
+			}
+		} else {
+			int tnode_size_u32 = dev->tnode_size / sizeof(u32);
+			u32 *map = (u32 *) tn;
+
+			for (i = 0; !has_data && i < tnode_size_u32; i++) {
+				if (map[i])
+					has_data++;
+			}
+		}
+
+		if (has_data == 0 && del0) {
+			/* Free and return NULL */
+
+			yaffs_free_tnode(dev, tn);
+			tn = NULL;
+		}
+
+	}
+
+	return tn;
+
+}
+
+static int yaffs_prune_tree(struct yaffs_dev *dev,
+			    struct yaffs_file_var *file_struct)
+{
+	int i;
+	int has_data;
+	int done = 0;
+	struct yaffs_tnode *tn;
+
+	if (file_struct->top_level > 0) {
+		file_struct->top =
+		    yaffs_prune_worker(dev, file_struct->top,
+				       file_struct->top_level, 0);
+
+		/* Now we have a tree with all the non-zero branches NULL but the height
+		 * is the same as it was.
+		 * Let's see if we can trim internal tnodes to shorten the tree.
+		 * We can do this if only the 0th element in the tnode is in use
+		 * (ie all the non-zero are NULL)
+		 */
+
+		while (file_struct->top_level && !done) {
+			tn = file_struct->top;
+
+			has_data = 0;
+			for (i = 1; i < YAFFS_NTNODES_INTERNAL; i++) {
+				if (tn->internal[i])
+					has_data++;
+			}
+
+			if (!has_data) {
+				file_struct->top = tn->internal[0];
+				file_struct->top_level--;
+				yaffs_free_tnode(dev, tn);
+			} else {
+				done = 1;
+			}
+		}
+	}
+
+	return YAFFS_OK;
+}
+
+/*-------------------- End of File Structure functions.-------------------*/
+
+/* AllocateEmptyObject gets us a clean Object. Tries to make allocate more if we run out */
+static struct yaffs_obj *yaffs_alloc_empty_obj(struct yaffs_dev *dev)
+{
+	struct yaffs_obj *obj = yaffs_alloc_raw_obj(dev);
+
+	if (obj) {
+		dev->n_obj++;
+
+		/* Now sweeten it up... */
+
+		memset(obj, 0, sizeof(struct yaffs_obj));
+		obj->being_created = 1;
+
+		obj->my_dev = dev;
+		obj->hdr_chunk = 0;
+		obj->variant_type = YAFFS_OBJECT_TYPE_UNKNOWN;
+		INIT_LIST_HEAD(&(obj->hard_links));
+		INIT_LIST_HEAD(&(obj->hash_link));
+		INIT_LIST_HEAD(&obj->siblings);
+
+		/* Now make the directory sane */
+		if (dev->root_dir) {
+			obj->parent = dev->root_dir;
+			list_add(&(obj->siblings),
+				 &dev->root_dir->variant.dir_variant.children);
+		}
+
+		/* Add it to the lost and found directory.
+		 * NB Can't put root or lost-n-found in lost-n-found so
+		 * check if lost-n-found exists first
+		 */
+		if (dev->lost_n_found)
+			yaffs_add_obj_to_dir(dev->lost_n_found, obj);
+
+		obj->being_created = 0;
+	}
+
+	dev->checkpoint_blocks_required = 0;	/* force recalculation */
+
+	return obj;
+}
+
+static int yaffs_find_nice_bucket(struct yaffs_dev *dev)
+{
+	int i;
+	int l = 999;
+	int lowest = 999999;
+
+	/* Search for the shortest list or one that
+	 * isn't too long.
+	 */
+
+	for (i = 0; i < 10 && lowest > 4; i++) {
+		dev->bucket_finder++;
+		dev->bucket_finder %= YAFFS_NOBJECT_BUCKETS;
+		if (dev->obj_bucket[dev->bucket_finder].count < lowest) {
+			lowest = dev->obj_bucket[dev->bucket_finder].count;
+			l = dev->bucket_finder;
+		}
+
+	}
+
+	return l;
+}
+
+static int yaffs_new_obj_id(struct yaffs_dev *dev)
+{
+	int bucket = yaffs_find_nice_bucket(dev);
+
+	/* Now find an object value that has not already been taken
+	 * by scanning the list.
+	 */
+
+	int found = 0;
+	struct list_head *i;
+
+	u32 n = (u32) bucket;
+
+	/* yaffs_check_obj_hash_sane();  */
+
+	while (!found) {
+		found = 1;
+		n += YAFFS_NOBJECT_BUCKETS;
+		if (1 || dev->obj_bucket[bucket].count > 0) {
+			list_for_each(i, &dev->obj_bucket[bucket].list) {
+				/* If there is already one in the list */
+				if (i && list_entry(i, struct yaffs_obj,
+						    hash_link)->obj_id == n) {
+					found = 0;
+				}
+			}
+		}
+	}
+
+	return n;
+}
+
+static void yaffs_hash_obj(struct yaffs_obj *in)
+{
+	int bucket = yaffs_hash_fn(in->obj_id);
+	struct yaffs_dev *dev = in->my_dev;
+
+	list_add(&in->hash_link, &dev->obj_bucket[bucket].list);
+	dev->obj_bucket[bucket].count++;
+}
+
+struct yaffs_obj *yaffs_find_by_number(struct yaffs_dev *dev, u32 number)
+{
+	int bucket = yaffs_hash_fn(number);
+	struct list_head *i;
+	struct yaffs_obj *in;
+
+	list_for_each(i, &dev->obj_bucket[bucket].list) {
+		/* Look if it is in the list */
+		if (i) {
+			in = list_entry(i, struct yaffs_obj, hash_link);
+			if (in->obj_id == number) {
+
+				/* Don't tell the VFS about this one if it is defered free */
+				if (in->defered_free)
+					return NULL;
+
+				return in;
+			}
+		}
+	}
+
+	return NULL;
+}
+
+struct yaffs_obj *yaffs_new_obj(struct yaffs_dev *dev, int number,
+				enum yaffs_obj_type type)
+{
+	struct yaffs_obj *the_obj = NULL;
+	struct yaffs_tnode *tn = NULL;
+
+	if (number < 0)
+		number = yaffs_new_obj_id(dev);
+
+	if (type == YAFFS_OBJECT_TYPE_FILE) {
+		tn = yaffs_get_tnode(dev);
+		if (!tn)
+			return NULL;
+	}
+
+	the_obj = yaffs_alloc_empty_obj(dev);
+	if (!the_obj) {
+		if (tn)
+			yaffs_free_tnode(dev, tn);
+		return NULL;
+	}
+
+	if (the_obj) {
+		the_obj->fake = 0;
+		the_obj->rename_allowed = 1;
+		the_obj->unlink_allowed = 1;
+		the_obj->obj_id = number;
+		yaffs_hash_obj(the_obj);
+		the_obj->variant_type = type;
+		yaffs_load_current_time(the_obj, 1, 1);
+
+		switch (type) {
+		case YAFFS_OBJECT_TYPE_FILE:
+			the_obj->variant.file_variant.file_size = 0;
+			the_obj->variant.file_variant.scanned_size = 0;
+			the_obj->variant.file_variant.shrink_size = ~0;	/* max */
+			the_obj->variant.file_variant.top_level = 0;
+			the_obj->variant.file_variant.top = tn;
+			break;
+		case YAFFS_OBJECT_TYPE_DIRECTORY:
+			INIT_LIST_HEAD(&the_obj->variant.dir_variant.children);
+			INIT_LIST_HEAD(&the_obj->variant.dir_variant.dirty);
+			break;
+		case YAFFS_OBJECT_TYPE_SYMLINK:
+		case YAFFS_OBJECT_TYPE_HARDLINK:
+		case YAFFS_OBJECT_TYPE_SPECIAL:
+			/* No action required */
+			break;
+		case YAFFS_OBJECT_TYPE_UNKNOWN:
+			/* todo this should not happen */
+			break;
+		}
+	}
+
+	return the_obj;
+}
+
+static struct yaffs_obj *yaffs_create_fake_dir(struct yaffs_dev *dev,
+					       int number, u32 mode)
+{
+
+	struct yaffs_obj *obj =
+	    yaffs_new_obj(dev, number, YAFFS_OBJECT_TYPE_DIRECTORY);
+	if (obj) {
+		obj->fake = 1;	/* it is fake so it might have no NAND presence... */
+		obj->rename_allowed = 0;	/* ... and we're not allowed to rename it... */
+		obj->unlink_allowed = 0;	/* ... or unlink it */
+		obj->deleted = 0;
+		obj->unlinked = 0;
+		obj->yst_mode = mode;
+		obj->my_dev = dev;
+		obj->hdr_chunk = 0;	/* Not a valid chunk. */
+	}
+
+	return obj;
+
+}
+
+
+static void yaffs_init_tnodes_and_objs(struct yaffs_dev *dev)
+{
+	int i;
+
+	dev->n_obj = 0;
+	dev->n_tnodes = 0;
+
+	yaffs_init_raw_tnodes_and_objs(dev);
+
+	for (i = 0; i < YAFFS_NOBJECT_BUCKETS; i++) {
+		INIT_LIST_HEAD(&dev->obj_bucket[i].list);
+		dev->obj_bucket[i].count = 0;
+	}
+}
+
+struct yaffs_obj *yaffs_find_or_create_by_number(struct yaffs_dev *dev,
+						 int number,
+						 enum yaffs_obj_type type)
+{
+	struct yaffs_obj *the_obj = NULL;
+
+	if (number > 0)
+		the_obj = yaffs_find_by_number(dev, number);
+
+	if (!the_obj)
+		the_obj = yaffs_new_obj(dev, number, type);
+
+	return the_obj;
+
+}
+
+YCHAR *yaffs_clone_str(const YCHAR * str)
+{
+	YCHAR *new_str = NULL;
+	int len;
+
+	if (!str)
+		str = _Y("");
+
+	len = strnlen(str, YAFFS_MAX_ALIAS_LENGTH);
+	new_str = kmalloc((len + 1) * sizeof(YCHAR), GFP_NOFS);
+	if (new_str) {
+		strncpy(new_str, str, len);
+		new_str[len] = 0;
+	}
+	return new_str;
+
+}
+/*
+ *yaffs_update_parent() handles fixing a directories mtime and ctime when a new
+ * link (ie. name) is created or deleted in the directory.
+ *
+ * ie.
+ *   create dir/a : update dir's mtime/ctime
+ *   rm dir/a:   update dir's mtime/ctime
+ *   modify dir/a: don't update dir's mtimme/ctime
+ *
+ * This can be handled immediately or defered. Defering helps reduce the number
+ * of updates when many files in a directory are changed within a brief period.
+ *
+ * If the directory updating is defered then yaffs_update_dirty_dirs must be
+ * called periodically.
+ */
+
+static void yaffs_update_parent(struct yaffs_obj *obj)
+{
+	struct yaffs_dev *dev;
+	if (!obj)
+		return;
+	dev = obj->my_dev;
+	obj->dirty = 1;
+	yaffs_load_current_time(obj, 0, 1);
+	if (dev->param.defered_dir_update) {
+		struct list_head *link = &obj->variant.dir_variant.dirty;
+
+		if (list_empty(link)) {
+			list_add(link, &dev->dirty_dirs);
+			yaffs_trace(YAFFS_TRACE_BACKGROUND,
+			  "Added object %d to dirty directories",
+			   obj->obj_id);
+		}
+
+	} else {
+		yaffs_update_oh(obj, NULL, 0, 0, 0, NULL);
+        }
+}
+
+void yaffs_update_dirty_dirs(struct yaffs_dev *dev)
+{
+	struct list_head *link;
+	struct yaffs_obj *obj;
+	struct yaffs_dir_var *d_s;
+	union yaffs_obj_var *o_v;
+
+	yaffs_trace(YAFFS_TRACE_BACKGROUND, "Update dirty directories");
+
+	while (!list_empty(&dev->dirty_dirs)) {
+		link = dev->dirty_dirs.next;
+		list_del_init(link);
+
+		d_s = list_entry(link, struct yaffs_dir_var, dirty);
+		o_v = list_entry(d_s, union yaffs_obj_var, dir_variant);
+		obj = list_entry(o_v, struct yaffs_obj, variant);
+
+		yaffs_trace(YAFFS_TRACE_BACKGROUND, "Update directory %d",
+			obj->obj_id);
+
+		if (obj->dirty)
+			yaffs_update_oh(obj, NULL, 0, 0, 0, NULL);
+	}
+}
+
+/*
+ * Mknod (create) a new object.
+ * equiv_obj only has meaning for a hard link;
+ * alias_str only has meaning for a symlink.
+ * rdev only has meaning for devices (a subset of special objects)
+ */
+
+static struct yaffs_obj *yaffs_create_obj(enum yaffs_obj_type type,
+					  struct yaffs_obj *parent,
+					  const YCHAR * name,
+					  u32 mode,
+					  u32 uid,
+					  u32 gid,
+					  struct yaffs_obj *equiv_obj,
+					  const YCHAR * alias_str, u32 rdev)
+{
+	struct yaffs_obj *in;
+	YCHAR *str = NULL;
+
+	struct yaffs_dev *dev = parent->my_dev;
+
+	/* Check if the entry exists. If it does then fail the call since we don't want a dup. */
+	if (yaffs_find_by_name(parent, name))
+		return NULL;
+
+	if (type == YAFFS_OBJECT_TYPE_SYMLINK) {
+		str = yaffs_clone_str(alias_str);
+		if (!str)
+			return NULL;
+	}
+
+	in = yaffs_new_obj(dev, -1, type);
+
+	if (!in) {
+		if (str)
+			kfree(str);
+		return NULL;
+	}
+
+	if (in) {
+		in->hdr_chunk = 0;
+		in->valid = 1;
+		in->variant_type = type;
+
+		in->yst_mode = mode;
+
+		yaffs_attribs_init(in, gid, uid, rdev);
+
+		in->n_data_chunks = 0;
+
+		yaffs_set_obj_name(in, name);
+		in->dirty = 1;
+
+		yaffs_add_obj_to_dir(parent, in);
+
+		in->my_dev = parent->my_dev;
+
+		switch (type) {
+		case YAFFS_OBJECT_TYPE_SYMLINK:
+			in->variant.symlink_variant.alias = str;
+			break;
+		case YAFFS_OBJECT_TYPE_HARDLINK:
+			in->variant.hardlink_variant.equiv_obj = equiv_obj;
+			in->variant.hardlink_variant.equiv_id =
+			    equiv_obj->obj_id;
+			list_add(&in->hard_links, &equiv_obj->hard_links);
+			break;
+		case YAFFS_OBJECT_TYPE_FILE:
+		case YAFFS_OBJECT_TYPE_DIRECTORY:
+		case YAFFS_OBJECT_TYPE_SPECIAL:
+		case YAFFS_OBJECT_TYPE_UNKNOWN:
+			/* do nothing */
+			break;
+		}
+
+		if (yaffs_update_oh(in, name, 0, 0, 0, NULL) < 0) {
+			/* Could not create the object header, fail the creation */
+			yaffs_del_obj(in);
+			in = NULL;
+		}
+
+		yaffs_update_parent(parent);
+	}
+
+	return in;
+}
+
+struct yaffs_obj *yaffs_create_file(struct yaffs_obj *parent,
+				    const YCHAR * name, u32 mode, u32 uid,
+				    u32 gid)
+{
+	return yaffs_create_obj(YAFFS_OBJECT_TYPE_FILE, parent, name, mode,
+				uid, gid, NULL, NULL, 0);
+}
+
+struct yaffs_obj *yaffs_create_dir(struct yaffs_obj *parent, const YCHAR * name,
+				   u32 mode, u32 uid, u32 gid)
+{
+	return yaffs_create_obj(YAFFS_OBJECT_TYPE_DIRECTORY, parent, name,
+				mode, uid, gid, NULL, NULL, 0);
+}
+
+struct yaffs_obj *yaffs_create_special(struct yaffs_obj *parent,
+				       const YCHAR * name, u32 mode, u32 uid,
+				       u32 gid, u32 rdev)
+{
+	return yaffs_create_obj(YAFFS_OBJECT_TYPE_SPECIAL, parent, name, mode,
+				uid, gid, NULL, NULL, rdev);
+}
+
+struct yaffs_obj *yaffs_create_symlink(struct yaffs_obj *parent,
+				       const YCHAR * name, u32 mode, u32 uid,
+				       u32 gid, const YCHAR * alias)
+{
+	return yaffs_create_obj(YAFFS_OBJECT_TYPE_SYMLINK, parent, name, mode,
+				uid, gid, NULL, alias, 0);
+}
+
+/* yaffs_link_obj returns the object id of the equivalent object.*/
+struct yaffs_obj *yaffs_link_obj(struct yaffs_obj *parent, const YCHAR * name,
+				 struct yaffs_obj *equiv_obj)
+{
+	/* Get the real object in case we were fed a hard link as an equivalent object */
+	equiv_obj = yaffs_get_equivalent_obj(equiv_obj);
+
+	if (yaffs_create_obj
+	    (YAFFS_OBJECT_TYPE_HARDLINK, parent, name, 0, 0, 0,
+	     equiv_obj, NULL, 0)) {
+		return equiv_obj;
+	} else {
+		return NULL;
+	}
+
+}
+
+
+
+/*------------------------- Block Management and Page Allocation ----------------*/
+
+static int yaffs_init_blocks(struct yaffs_dev *dev)
+{
+	int n_blocks = dev->internal_end_block - dev->internal_start_block + 1;
+
+	dev->block_info = NULL;
+	dev->chunk_bits = NULL;
+
+	dev->alloc_block = -1;	/* force it to get a new one */
+
+	/* If the first allocation strategy fails, thry the alternate one */
+	dev->block_info =
+		kmalloc(n_blocks * sizeof(struct yaffs_block_info), GFP_NOFS);
+	if (!dev->block_info) {
+		dev->block_info =
+		    vmalloc(n_blocks * sizeof(struct yaffs_block_info));
+		dev->block_info_alt = 1;
+	} else {
+		dev->block_info_alt = 0;
+        }
+
+	if (dev->block_info) {
+		/* Set up dynamic blockinfo stuff. Round up bytes. */
+		dev->chunk_bit_stride = (dev->param.chunks_per_block + 7) / 8;
+		dev->chunk_bits =
+			kmalloc(dev->chunk_bit_stride * n_blocks, GFP_NOFS);
+		if (!dev->chunk_bits) {
+			dev->chunk_bits =
+			    vmalloc(dev->chunk_bit_stride * n_blocks);
+			dev->chunk_bits_alt = 1;
+		} else {
+			dev->chunk_bits_alt = 0;
+                }
+	}
+
+	if (dev->block_info && dev->chunk_bits) {
+		memset(dev->block_info, 0,
+		       n_blocks * sizeof(struct yaffs_block_info));
+		memset(dev->chunk_bits, 0, dev->chunk_bit_stride * n_blocks);
+		return YAFFS_OK;
+	}
+
+	return YAFFS_FAIL;
+}
+
+static void yaffs_deinit_blocks(struct yaffs_dev *dev)
+{
+	if (dev->block_info_alt && dev->block_info)
+		vfree(dev->block_info);
+	else if (dev->block_info)
+		kfree(dev->block_info);
+
+	dev->block_info_alt = 0;
+
+	dev->block_info = NULL;
+
+	if (dev->chunk_bits_alt && dev->chunk_bits)
+		vfree(dev->chunk_bits);
+	else if (dev->chunk_bits)
+		kfree(dev->chunk_bits);
+	dev->chunk_bits_alt = 0;
+	dev->chunk_bits = NULL;
+}
+
+void yaffs_block_became_dirty(struct yaffs_dev *dev, int block_no)
+{
+	struct yaffs_block_info *bi = yaffs_get_block_info(dev, block_no);
+
+	int erased_ok = 0;
+
+	/* If the block is still healthy erase it and mark as clean.
+	 * If the block has had a data failure, then retire it.
+	 */
+
+	yaffs_trace(YAFFS_TRACE_GC | YAFFS_TRACE_ERASE,
+		"yaffs_block_became_dirty block %d state %d %s",
+		block_no, bi->block_state,
+		(bi->needs_retiring) ? "needs retiring" : "");
+
+	yaffs2_clear_oldest_dirty_seq(dev, bi);
+
+	bi->block_state = YAFFS_BLOCK_STATE_DIRTY;
+
+	/* If this is the block being garbage collected then stop gc'ing this block */
+	if (block_no == dev->gc_block)
+		dev->gc_block = 0;
+
+	/* If this block is currently the best candidate for gc then drop as a candidate */
+	if (block_no == dev->gc_dirtiest) {
+		dev->gc_dirtiest = 0;
+		dev->gc_pages_in_use = 0;
+	}
+
+	if (!bi->needs_retiring) {
+		yaffs2_checkpt_invalidate(dev);
+		erased_ok = yaffs_erase_block(dev, block_no);
+		if (!erased_ok) {
+			dev->n_erase_failures++;
+			yaffs_trace(YAFFS_TRACE_ERROR | YAFFS_TRACE_BAD_BLOCKS,
+			  "**>> Erasure failed %d", block_no);
+		}
+	}
+
+	if (erased_ok &&
+	    ((yaffs_trace_mask & YAFFS_TRACE_ERASE)
+	     || !yaffs_skip_verification(dev))) {
+		int i;
+		for (i = 0; i < dev->param.chunks_per_block; i++) {
+			if (!yaffs_check_chunk_erased
+			    (dev, block_no * dev->param.chunks_per_block + i)) {
+				yaffs_trace(YAFFS_TRACE_ERROR,
+					">>Block %d erasure supposedly OK, but chunk %d not erased",
+					block_no, i);
+			}
+		}
+	}
+
+	if (erased_ok) {
+		/* Clean it up... */
+		bi->block_state = YAFFS_BLOCK_STATE_EMPTY;
+		bi->seq_number = 0;
+		dev->n_erased_blocks++;
+		bi->pages_in_use = 0;
+		bi->soft_del_pages = 0;
+		bi->has_shrink_hdr = 0;
+		bi->skip_erased_check = 1;	/* Clean, so no need to check */
+		bi->gc_prioritise = 0;
+		yaffs_clear_chunk_bits(dev, block_no);
+
+		yaffs_trace(YAFFS_TRACE_ERASE,
+			"Erased block %d", block_no);
+	} else {
+		/* We lost a block of free space */
+		dev->n_free_chunks -= dev->param.chunks_per_block;
+		yaffs_retire_block(dev, block_no);
+		yaffs_trace(YAFFS_TRACE_ERROR | YAFFS_TRACE_BAD_BLOCKS,
+			"**>> Block %d retired", block_no);
+	}
+}
+
+
+
+static int yaffs_gc_block(struct yaffs_dev *dev, int block, int whole_block)
+{
+	int old_chunk;
+	int new_chunk;
+	int mark_flash;
+	int ret_val = YAFFS_OK;
+	int i;
+	int is_checkpt_block;
+	int matching_chunk;
+	int max_copies;
+
+	int chunks_before = yaffs_get_erased_chunks(dev);
+	int chunks_after;
+
+	struct yaffs_ext_tags tags;
+
+	struct yaffs_block_info *bi = yaffs_get_block_info(dev, block);
+
+	struct yaffs_obj *object;
+
+	is_checkpt_block = (bi->block_state == YAFFS_BLOCK_STATE_CHECKPOINT);
+
+	yaffs_trace(YAFFS_TRACE_TRACING,
+		"Collecting block %d, in use %d, shrink %d, whole_block %d",
+		block, bi->pages_in_use, bi->has_shrink_hdr,
+		whole_block);
+
+	/*yaffs_verify_free_chunks(dev); */
+
+	if (bi->block_state == YAFFS_BLOCK_STATE_FULL)
+		bi->block_state = YAFFS_BLOCK_STATE_COLLECTING;
+
+	bi->has_shrink_hdr = 0;	/* clear the flag so that the block can erase */
+
+	dev->gc_disable = 1;
+
+	if (is_checkpt_block || !yaffs_still_some_chunks(dev, block)) {
+		yaffs_trace(YAFFS_TRACE_TRACING,
+			"Collecting block %d that has no chunks in use",
+		   	block);
+		yaffs_block_became_dirty(dev, block);
+	} else {
+
+		u8 *buffer = yaffs_get_temp_buffer(dev, __LINE__);
+
+		yaffs_verify_blk(dev, bi, block);
+
+		max_copies = (whole_block) ? dev->param.chunks_per_block : 5;
+		old_chunk = block * dev->param.chunks_per_block + dev->gc_chunk;
+
+		for ( /* init already done */ ;
+		     ret_val == YAFFS_OK &&
+		     dev->gc_chunk < dev->param.chunks_per_block &&
+		     (bi->block_state == YAFFS_BLOCK_STATE_COLLECTING) &&
+		     max_copies > 0; dev->gc_chunk++, old_chunk++) {
+			if (yaffs_check_chunk_bit(dev, block, dev->gc_chunk)) {
+
+				/* This page is in use and might need to be copied off */
+
+				max_copies--;
+
+				mark_flash = 1;
+
+				yaffs_init_tags(&tags);
+
+				yaffs_rd_chunk_tags_nand(dev, old_chunk,
+							 buffer, &tags);
+
+				object = yaffs_find_by_number(dev, tags.obj_id);
+
+				yaffs_trace(YAFFS_TRACE_GC_DETAIL,
+					"Collecting chunk in block %d, %d %d %d ",
+					dev->gc_chunk, tags.obj_id,
+					tags.chunk_id, tags.n_bytes);
+
+				if (object && !yaffs_skip_verification(dev)) {
+					if (tags.chunk_id == 0)
+						matching_chunk =
+						    object->hdr_chunk;
+					else if (object->soft_del)
+						matching_chunk = old_chunk;	/* Defeat the test */
+					else
+						matching_chunk =
+						    yaffs_find_chunk_in_file
+						    (object, tags.chunk_id,
+						     NULL);
+
+					if (old_chunk != matching_chunk)
+						yaffs_trace(YAFFS_TRACE_ERROR,
+							"gc: page in gc mismatch: %d %d %d %d",
+							old_chunk,
+							matching_chunk,
+							tags.obj_id,
+						   	tags.chunk_id);
+
+				}
+
+				if (!object) {
+					yaffs_trace(YAFFS_TRACE_ERROR,
+						"page %d in gc has no object: %d %d %d ",
+						old_chunk,
+						tags.obj_id, tags.chunk_id,
+						tags.n_bytes);
+				}
+
+				if (object &&
+				    object->deleted &&
+				    object->soft_del && tags.chunk_id != 0) {
+					/* Data chunk in a soft deleted file, throw it away
+					 * It's a soft deleted data chunk,
+					 * No need to copy this, just forget about it and
+					 * fix up the object.
+					 */
+
+					/* Free chunks already includes softdeleted chunks.
+					 * How ever this chunk is going to soon be really deleted
+					 * which will increment free chunks.
+					 * We have to decrement free chunks so this works out properly.
+					 */
+					dev->n_free_chunks--;
+					bi->soft_del_pages--;
+
+					object->n_data_chunks--;
+
+					if (object->n_data_chunks <= 0) {
+						/* remeber to clean up the object */
+						dev->gc_cleanup_list[dev->
+								     n_clean_ups]
+						    = tags.obj_id;
+						dev->n_clean_ups++;
+					}
+					mark_flash = 0;
+				} else if (0) {
+					/* Todo object && object->deleted && object->n_data_chunks == 0 */
+					/* Deleted object header with no data chunks.
+					 * Can be discarded and the file deleted.
+					 */
+					object->hdr_chunk = 0;
+					yaffs_free_tnode(object->my_dev,
+							 object->
+							 variant.file_variant.
+							 top);
+					object->variant.file_variant.top = NULL;
+					yaffs_generic_obj_del(object);
+
+				} else if (object) {
+					/* It's either a data chunk in a live file or
+					 * an ObjectHeader, so we're interested in it.
+					 * NB Need to keep the ObjectHeaders of deleted files
+					 * until the whole file has been deleted off
+					 */
+					tags.serial_number++;
+
+					dev->n_gc_copies++;
+
+					if (tags.chunk_id == 0) {
+						/* It is an object Id,
+						 * We need to nuke the shrinkheader flags first
+						 * Also need to clean up shadowing.
+						 * We no longer want the shrink_header flag since its work is done
+						 * and if it is left in place it will mess up scanning.
+						 */
+
+						struct yaffs_obj_hdr *oh;
+						oh = (struct yaffs_obj_hdr *)
+						    buffer;
+
+						oh->is_shrink = 0;
+						tags.extra_is_shrink = 0;
+
+						oh->shadows_obj = 0;
+						oh->inband_shadowed_obj_id = 0;
+						tags.extra_shadows = 0;
+
+						/* Update file size */
+						if (object->variant_type ==
+						    YAFFS_OBJECT_TYPE_FILE) {
+							oh->file_size =
+							    object->variant.
+							    file_variant.
+							    file_size;
+							tags.extra_length =
+							    oh->file_size;
+						}
+
+						yaffs_verify_oh(object, oh,
+								&tags, 1);
+						new_chunk =
+						    yaffs_write_new_chunk(dev,
+									  (u8 *)
+									  oh,
+									  &tags,
+									  1);
+					} else {
+						new_chunk =
+						    yaffs_write_new_chunk(dev,
+									  buffer,
+									  &tags,
+									  1);
+                                        }
+
+					if (new_chunk < 0) {
+						ret_val = YAFFS_FAIL;
+					} else {
+
+						/* Ok, now fix up the Tnodes etc. */
+
+						if (tags.chunk_id == 0) {
+							/* It's a header */
+							object->hdr_chunk =
+							    new_chunk;
+							object->serial =
+							    tags.serial_number;
+						} else {
+							/* It's a data chunk */
+							int ok;
+							ok = yaffs_put_chunk_in_file(object, tags.chunk_id, new_chunk, 0);
+						}
+					}
+				}
+
+				if (ret_val == YAFFS_OK)
+					yaffs_chunk_del(dev, old_chunk,
+							mark_flash, __LINE__);
+
+			}
+		}
+
+		yaffs_release_temp_buffer(dev, buffer, __LINE__);
+
+	}
+
+	yaffs_verify_collected_blk(dev, bi, block);
+
+	if (bi->block_state == YAFFS_BLOCK_STATE_COLLECTING) {
+		/*
+		 * The gc did not complete. Set block state back to FULL
+		 * because checkpointing does not restore gc.
+		 */
+		bi->block_state = YAFFS_BLOCK_STATE_FULL;
+	} else {
+		/* The gc completed. */
+		/* Do any required cleanups */
+		for (i = 0; i < dev->n_clean_ups; i++) {
+			/* Time to delete the file too */
+			object =
+			    yaffs_find_by_number(dev, dev->gc_cleanup_list[i]);
+			if (object) {
+				yaffs_free_tnode(dev,
+						 object->variant.
+						 file_variant.top);
+				object->variant.file_variant.top = NULL;
+				yaffs_trace(YAFFS_TRACE_GC,
+					"yaffs: About to finally delete object %d",
+					object->obj_id);
+				yaffs_generic_obj_del(object);
+				object->my_dev->n_deleted_files--;
+			}
+
+		}
+
+		chunks_after = yaffs_get_erased_chunks(dev);
+		if (chunks_before >= chunks_after)
+			yaffs_trace(YAFFS_TRACE_GC,
+				"gc did not increase free chunks before %d after %d",
+				chunks_before, chunks_after);
+		dev->gc_block = 0;
+		dev->gc_chunk = 0;
+		dev->n_clean_ups = 0;
+	}
+
+	dev->gc_disable = 0;
+
+	return ret_val;
+}
+
+/*
+ * FindBlockForgarbageCollection is used to select the dirtiest block (or close enough)
+ * for garbage collection.
+ */
+
+static unsigned yaffs_find_gc_block(struct yaffs_dev *dev,
+				    int aggressive, int background)
+{
+	int i;
+	int iterations;
+	unsigned selected = 0;
+	int prioritised = 0;
+	int prioritised_exist = 0;
+	struct yaffs_block_info *bi;
+	int threshold;
+
+	/* First let's see if we need to grab a prioritised block */
+	if (dev->has_pending_prioritised_gc && !aggressive) {
+		dev->gc_dirtiest = 0;
+		bi = dev->block_info;
+		for (i = dev->internal_start_block;
+		     i <= dev->internal_end_block && !selected; i++) {
+
+			if (bi->gc_prioritise) {
+				prioritised_exist = 1;
+				if (bi->block_state == YAFFS_BLOCK_STATE_FULL &&
+				    yaffs_block_ok_for_gc(dev, bi)) {
+					selected = i;
+					prioritised = 1;
+				}
+			}
+			bi++;
+		}
+
+		/*
+		 * If there is a prioritised block and none was selected then
+		 * this happened because there is at least one old dirty block gumming
+		 * up the works. Let's gc the oldest dirty block.
+		 */
+
+		if (prioritised_exist &&
+		    !selected && dev->oldest_dirty_block > 0)
+			selected = dev->oldest_dirty_block;
+
+		if (!prioritised_exist)	/* None found, so we can clear this */
+			dev->has_pending_prioritised_gc = 0;
+	}
+
+	/* If we're doing aggressive GC then we are happy to take a less-dirty block, and
+	 * search harder.
+	 * else (we're doing a leasurely gc), then we only bother to do this if the
+	 * block has only a few pages in use.
+	 */
+
+	if (!selected) {
+		int pages_used;
+		int n_blocks =
+		    dev->internal_end_block - dev->internal_start_block + 1;
+		if (aggressive) {
+			threshold = dev->param.chunks_per_block;
+			iterations = n_blocks;
+		} else {
+			int max_threshold;
+
+			if (background)
+				max_threshold = dev->param.chunks_per_block / 2;
+			else
+				max_threshold = dev->param.chunks_per_block / 8;
+
+			if (max_threshold < YAFFS_GC_PASSIVE_THRESHOLD)
+				max_threshold = YAFFS_GC_PASSIVE_THRESHOLD;
+
+			threshold = background ? (dev->gc_not_done + 2) * 2 : 0;
+			if (threshold < YAFFS_GC_PASSIVE_THRESHOLD)
+				threshold = YAFFS_GC_PASSIVE_THRESHOLD;
+			if (threshold > max_threshold)
+				threshold = max_threshold;
+
+			iterations = n_blocks / 16 + 1;
+			if (iterations > 100)
+				iterations = 100;
+		}
+
+		for (i = 0;
+		     i < iterations &&
+		     (dev->gc_dirtiest < 1 ||
+		      dev->gc_pages_in_use > YAFFS_GC_GOOD_ENOUGH); i++) {
+			dev->gc_block_finder++;
+			if (dev->gc_block_finder < dev->internal_start_block ||
+			    dev->gc_block_finder > dev->internal_end_block)
+				dev->gc_block_finder =
+				    dev->internal_start_block;
+
+			bi = yaffs_get_block_info(dev, dev->gc_block_finder);
+
+			pages_used = bi->pages_in_use - bi->soft_del_pages;
+
+			if (bi->block_state == YAFFS_BLOCK_STATE_FULL &&
+			    pages_used < dev->param.chunks_per_block &&
+			    (dev->gc_dirtiest < 1
+			     || pages_used < dev->gc_pages_in_use)
+			    && yaffs_block_ok_for_gc(dev, bi)) {
+				dev->gc_dirtiest = dev->gc_block_finder;
+				dev->gc_pages_in_use = pages_used;
+			}
+		}
+
+		if (dev->gc_dirtiest > 0 && dev->gc_pages_in_use <= threshold)
+			selected = dev->gc_dirtiest;
+	}
+
+	/*
+	 * If nothing has been selected for a while, try selecting the oldest dirty
+	 * because that's gumming up the works.
+	 */
+
+	if (!selected && dev->param.is_yaffs2 &&
+	    dev->gc_not_done >= (background ? 10 : 20)) {
+		yaffs2_find_oldest_dirty_seq(dev);
+		if (dev->oldest_dirty_block > 0) {
+			selected = dev->oldest_dirty_block;
+			dev->gc_dirtiest = selected;
+			dev->oldest_dirty_gc_count++;
+			bi = yaffs_get_block_info(dev, selected);
+			dev->gc_pages_in_use =
+			    bi->pages_in_use - bi->soft_del_pages;
+		} else {
+			dev->gc_not_done = 0;
+                }
+	}
+
+	if (selected) {
+		yaffs_trace(YAFFS_TRACE_GC,
+			"GC Selected block %d with %d free, prioritised:%d",
+			selected,
+			dev->param.chunks_per_block - dev->gc_pages_in_use,
+			prioritised);
+
+		dev->n_gc_blocks++;
+		if (background)
+			dev->bg_gcs++;
+
+		dev->gc_dirtiest = 0;
+		dev->gc_pages_in_use = 0;
+		dev->gc_not_done = 0;
+		if (dev->refresh_skip > 0)
+			dev->refresh_skip--;
+	} else {
+		dev->gc_not_done++;
+		yaffs_trace(YAFFS_TRACE_GC,
+			"GC none: finder %d skip %d threshold %d dirtiest %d using %d oldest %d%s",
+			dev->gc_block_finder, dev->gc_not_done, threshold,
+			dev->gc_dirtiest, dev->gc_pages_in_use,
+			dev->oldest_dirty_block, background ? " bg" : "");
+	}
+
+	return selected;
+}
+
+/* New garbage collector
+ * If we're very low on erased blocks then we do aggressive garbage collection
+ * otherwise we do "leasurely" garbage collection.
+ * Aggressive gc looks further (whole array) and will accept less dirty blocks.
+ * Passive gc only inspects smaller areas and will only accept more dirty blocks.
+ *
+ * The idea is to help clear out space in a more spread-out manner.
+ * Dunno if it really does anything useful.
+ */
+static int yaffs_check_gc(struct yaffs_dev *dev, int background)
+{
+	int aggressive = 0;
+	int gc_ok = YAFFS_OK;
+	int max_tries = 0;
+	int min_erased;
+	int erased_chunks;
+	int checkpt_block_adjust;
+
+	if (dev->param.gc_control && (dev->param.gc_control(dev) & 1) == 0)
+		return YAFFS_OK;
+
+	if (dev->gc_disable) {
+		/* Bail out so we don't get recursive gc */
+		return YAFFS_OK;
+	}
+
+	/* This loop should pass the first time.
+	 * We'll only see looping here if the collection does not increase space.
+	 */
+
+	do {
+		max_tries++;
+
+		checkpt_block_adjust = yaffs_calc_checkpt_blocks_required(dev);
+
+		min_erased =
+		    dev->param.n_reserved_blocks + checkpt_block_adjust + 1;
+		erased_chunks =
+		    dev->n_erased_blocks * dev->param.chunks_per_block;
+
+		/* If we need a block soon then do aggressive gc. */
+		if (dev->n_erased_blocks < min_erased)
+			aggressive = 1;
+		else {
+			if (!background
+			    && erased_chunks > (dev->n_free_chunks / 4))
+				break;
+
+			if (dev->gc_skip > 20)
+				dev->gc_skip = 20;
+			if (erased_chunks < dev->n_free_chunks / 2 ||
+			    dev->gc_skip < 1 || background)
+				aggressive = 0;
+			else {
+				dev->gc_skip--;
+				break;
+			}
+		}
+
+		dev->gc_skip = 5;
+
+		/* If we don't already have a block being gc'd then see if we should start another */
+
+		if (dev->gc_block < 1 && !aggressive) {
+			dev->gc_block = yaffs2_find_refresh_block(dev);
+			dev->gc_chunk = 0;
+			dev->n_clean_ups = 0;
+		}
+		if (dev->gc_block < 1) {
+			dev->gc_block =
+			    yaffs_find_gc_block(dev, aggressive, background);
+			dev->gc_chunk = 0;
+			dev->n_clean_ups = 0;
+		}
+
+		if (dev->gc_block > 0) {
+			dev->all_gcs++;
+			if (!aggressive)
+				dev->passive_gc_count++;
+
+			yaffs_trace(YAFFS_TRACE_GC,
+				"yaffs: GC n_erased_blocks %d aggressive %d",
+				dev->n_erased_blocks, aggressive);
+
+			gc_ok = yaffs_gc_block(dev, dev->gc_block, aggressive);
+		}
+
+		if (dev->n_erased_blocks < (dev->param.n_reserved_blocks)
+		    && dev->gc_block > 0) {
+			yaffs_trace(YAFFS_TRACE_GC,
+				"yaffs: GC !!!no reclaim!!! n_erased_blocks %d after try %d block %d",
+				dev->n_erased_blocks, max_tries,
+				dev->gc_block);
+		}
+	} while ((dev->n_erased_blocks < dev->param.n_reserved_blocks) &&
+		 (dev->gc_block > 0) && (max_tries < 2));
+
+	return aggressive ? gc_ok : YAFFS_OK;
+}
+
+/*
+ * yaffs_bg_gc()
+ * Garbage collects. Intended to be called from a background thread.
+ * Returns non-zero if at least half the free chunks are erased.
+ */
+int yaffs_bg_gc(struct yaffs_dev *dev, unsigned urgency)
+{
+	int erased_chunks = dev->n_erased_blocks * dev->param.chunks_per_block;
+
+	yaffs_trace(YAFFS_TRACE_BACKGROUND, "Background gc %u", urgency);
+
+	yaffs_check_gc(dev, 1);
+	return erased_chunks > dev->n_free_chunks / 2;
+}
+
+/*-------------------- Data file manipulation -----------------*/
+
+static int yaffs_rd_data_obj(struct yaffs_obj *in, int inode_chunk, u8 * buffer)
+{
+	int nand_chunk = yaffs_find_chunk_in_file(in, inode_chunk, NULL);
+
+	if (nand_chunk >= 0)
+		return yaffs_rd_chunk_tags_nand(in->my_dev, nand_chunk,
+						buffer, NULL);
+	else {
+		yaffs_trace(YAFFS_TRACE_NANDACCESS,
+			"Chunk %d not found zero instead",
+			nand_chunk);
+		/* get sane (zero) data if you read a hole */
+		memset(buffer, 0, in->my_dev->data_bytes_per_chunk);
+		return 0;
+	}
+
+}
+
+void yaffs_chunk_del(struct yaffs_dev *dev, int chunk_id, int mark_flash,
+		     int lyn)
+{
+	int block;
+	int page;
+	struct yaffs_ext_tags tags;
+	struct yaffs_block_info *bi;
+
+	if (chunk_id <= 0)
+		return;
+
+	dev->n_deletions++;
+	block = chunk_id / dev->param.chunks_per_block;
+	page = chunk_id % dev->param.chunks_per_block;
+
+	if (!yaffs_check_chunk_bit(dev, block, page))
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Deleting invalid chunk %d", chunk_id);
+
+	bi = yaffs_get_block_info(dev, block);
+
+	yaffs2_update_oldest_dirty_seq(dev, block, bi);
+
+	yaffs_trace(YAFFS_TRACE_DELETION,
+		"line %d delete of chunk %d",
+		lyn, chunk_id);
+
+	if (!dev->param.is_yaffs2 && mark_flash &&
+	    bi->block_state != YAFFS_BLOCK_STATE_COLLECTING) {
+
+		yaffs_init_tags(&tags);
+
+		tags.is_deleted = 1;
+
+		yaffs_wr_chunk_tags_nand(dev, chunk_id, NULL, &tags);
+		yaffs_handle_chunk_update(dev, chunk_id, &tags);
+	} else {
+		dev->n_unmarked_deletions++;
+	}
+
+	/* Pull out of the management area.
+	 * If the whole block became dirty, this will kick off an erasure.
+	 */
+	if (bi->block_state == YAFFS_BLOCK_STATE_ALLOCATING ||
+	    bi->block_state == YAFFS_BLOCK_STATE_FULL ||
+	    bi->block_state == YAFFS_BLOCK_STATE_NEEDS_SCANNING ||
+	    bi->block_state == YAFFS_BLOCK_STATE_COLLECTING) {
+		dev->n_free_chunks++;
+
+		yaffs_clear_chunk_bit(dev, block, page);
+
+		bi->pages_in_use--;
+
+		if (bi->pages_in_use == 0 &&
+		    !bi->has_shrink_hdr &&
+		    bi->block_state != YAFFS_BLOCK_STATE_ALLOCATING &&
+		    bi->block_state != YAFFS_BLOCK_STATE_NEEDS_SCANNING) {
+			yaffs_block_became_dirty(dev, block);
+		}
+
+	}
+
+}
+
+static int yaffs_wr_data_obj(struct yaffs_obj *in, int inode_chunk,
+			     const u8 * buffer, int n_bytes, int use_reserve)
+{
+	/* Find old chunk Need to do this to get serial number
+	 * Write new one and patch into tree.
+	 * Invalidate old tags.
+	 */
+
+	int prev_chunk_id;
+	struct yaffs_ext_tags prev_tags;
+
+	int new_chunk_id;
+	struct yaffs_ext_tags new_tags;
+
+	struct yaffs_dev *dev = in->my_dev;
+
+	yaffs_check_gc(dev, 0);
+
+	/* Get the previous chunk at this location in the file if it exists.
+	 * If it does not exist then put a zero into the tree. This creates
+	 * the tnode now, rather than later when it is harder to clean up.
+	 */
+	prev_chunk_id = yaffs_find_chunk_in_file(in, inode_chunk, &prev_tags);
+	if (prev_chunk_id < 1 &&
+	    !yaffs_put_chunk_in_file(in, inode_chunk, 0, 0))
+		return 0;
+
+	/* Set up new tags */
+	yaffs_init_tags(&new_tags);
+
+	new_tags.chunk_id = inode_chunk;
+	new_tags.obj_id = in->obj_id;
+	new_tags.serial_number =
+	    (prev_chunk_id > 0) ? prev_tags.serial_number + 1 : 1;
+	new_tags.n_bytes = n_bytes;
+
+	if (n_bytes < 1 || n_bytes > dev->param.total_bytes_per_chunk) {
+		yaffs_trace(YAFFS_TRACE_ERROR,
+		  "Writing %d bytes to chunk!!!!!!!!!",
+		   n_bytes);
+		YBUG();
+	}
+
+	new_chunk_id =
+	    yaffs_write_new_chunk(dev, buffer, &new_tags, use_reserve);
+
+	if (new_chunk_id > 0) {
+		yaffs_put_chunk_in_file(in, inode_chunk, new_chunk_id, 0);
+
+		if (prev_chunk_id > 0)
+			yaffs_chunk_del(dev, prev_chunk_id, 1, __LINE__);
+
+		yaffs_verify_file_sane(in);
+	}
+	return new_chunk_id;
+
+}
+
+
+
+static int yaffs_do_xattrib_mod(struct yaffs_obj *obj, int set,
+				const YCHAR * name, const void *value, int size,
+				int flags)
+{
+	struct yaffs_xattr_mod xmod;
+
+	int result;
+
+	xmod.set = set;
+	xmod.name = name;
+	xmod.data = value;
+	xmod.size = size;
+	xmod.flags = flags;
+	xmod.result = -ENOSPC;
+
+	result = yaffs_update_oh(obj, NULL, 0, 0, 0, &xmod);
+
+	if (result > 0)
+		return xmod.result;
+	else
+		return -ENOSPC;
+}
+
+static int yaffs_apply_xattrib_mod(struct yaffs_obj *obj, char *buffer,
+				   struct yaffs_xattr_mod *xmod)
+{
+	int retval = 0;
+	int x_offs = sizeof(struct yaffs_obj_hdr);
+	struct yaffs_dev *dev = obj->my_dev;
+	int x_size = dev->data_bytes_per_chunk - sizeof(struct yaffs_obj_hdr);
+
+	char *x_buffer = buffer + x_offs;
+
+	if (xmod->set)
+		retval =
+		    nval_set(x_buffer, x_size, xmod->name, xmod->data,
+			     xmod->size, xmod->flags);
+	else
+		retval = nval_del(x_buffer, x_size, xmod->name);
+
+	obj->has_xattr = nval_hasvalues(x_buffer, x_size);
+	obj->xattr_known = 1;
+
+	xmod->result = retval;
+
+	return retval;
+}
+
+static int yaffs_do_xattrib_fetch(struct yaffs_obj *obj, const YCHAR * name,
+				  void *value, int size)
+{
+	char *buffer = NULL;
+	int result;
+	struct yaffs_ext_tags tags;
+	struct yaffs_dev *dev = obj->my_dev;
+	int x_offs = sizeof(struct yaffs_obj_hdr);
+	int x_size = dev->data_bytes_per_chunk - sizeof(struct yaffs_obj_hdr);
+
+	char *x_buffer;
+
+	int retval = 0;
+
+	if (obj->hdr_chunk < 1)
+		return -ENODATA;
+
+	/* If we know that the object has no xattribs then don't do all the
+	 * reading and parsing.
+	 */
+	if (obj->xattr_known && !obj->has_xattr) {
+		if (name)
+			return -ENODATA;
+		else
+			return 0;
+	}
+
+	buffer = (char *)yaffs_get_temp_buffer(dev, __LINE__);
+	if (!buffer)
+		return -ENOMEM;
+
+	result =
+	    yaffs_rd_chunk_tags_nand(dev, obj->hdr_chunk, (u8 *) buffer, &tags);
+
+	if (result != YAFFS_OK)
+		retval = -ENOENT;
+	else {
+		x_buffer = buffer + x_offs;
+
+		if (!obj->xattr_known) {
+			obj->has_xattr = nval_hasvalues(x_buffer, x_size);
+			obj->xattr_known = 1;
+		}
+
+		if (name)
+			retval = nval_get(x_buffer, x_size, name, value, size);
+		else
+			retval = nval_list(x_buffer, x_size, value, size);
+	}
+	yaffs_release_temp_buffer(dev, (u8 *) buffer, __LINE__);
+	return retval;
+}
+
+int yaffs_set_xattrib(struct yaffs_obj *obj, const YCHAR * name,
+		      const void *value, int size, int flags)
+{
+	return yaffs_do_xattrib_mod(obj, 1, name, value, size, flags);
+}
+
+int yaffs_remove_xattrib(struct yaffs_obj *obj, const YCHAR * name)
+{
+	return yaffs_do_xattrib_mod(obj, 0, name, NULL, 0, 0);
+}
+
+int yaffs_get_xattrib(struct yaffs_obj *obj, const YCHAR * name, void *value,
+		      int size)
+{
+	return yaffs_do_xattrib_fetch(obj, name, value, size);
+}
+
+int yaffs_list_xattrib(struct yaffs_obj *obj, char *buffer, int size)
+{
+	return yaffs_do_xattrib_fetch(obj, NULL, buffer, size);
+}
+
+static void yaffs_check_obj_details_loaded(struct yaffs_obj *in)
+{
+	u8 *chunk_data;
+	struct yaffs_obj_hdr *oh;
+	struct yaffs_dev *dev;
+	struct yaffs_ext_tags tags;
+	int result;
+	int alloc_failed = 0;
+
+	if (!in)
+		return;
+
+	dev = in->my_dev;
+
+	if (in->lazy_loaded && in->hdr_chunk > 0) {
+		in->lazy_loaded = 0;
+		chunk_data = yaffs_get_temp_buffer(dev, __LINE__);
+
+		result =
+		    yaffs_rd_chunk_tags_nand(dev, in->hdr_chunk, chunk_data,
+					     &tags);
+		oh = (struct yaffs_obj_hdr *)chunk_data;
+
+		in->yst_mode = oh->yst_mode;
+		yaffs_load_attribs(in, oh);
+		yaffs_set_obj_name_from_oh(in, oh);
+
+		if (in->variant_type == YAFFS_OBJECT_TYPE_SYMLINK) {
+			in->variant.symlink_variant.alias =
+			    yaffs_clone_str(oh->alias);
+			if (!in->variant.symlink_variant.alias)
+				alloc_failed = 1;	/* Not returned to caller */
+		}
+
+		yaffs_release_temp_buffer(dev, chunk_data, __LINE__);
+	}
+}
+
+static void yaffs_load_name_from_oh(struct yaffs_dev *dev, YCHAR * name,
+				    const YCHAR * oh_name, int buff_size)
+{
+#ifdef CONFIG_YAFFS_AUTO_UNICODE
+	if (dev->param.auto_unicode) {
+		if (*oh_name) {
+			/* It is an ASCII name, do an ASCII to
+			 * unicode conversion */
+			const char *ascii_oh_name = (const char *)oh_name;
+			int n = buff_size - 1;
+			while (n > 0 && *ascii_oh_name) {
+				*name = *ascii_oh_name;
+				name++;
+				ascii_oh_name++;
+				n--;
+			}
+		} else {
+			strncpy(name, oh_name + 1, buff_size - 1);
+                }
+	} else {
+#else
+        {
+#endif
+		strncpy(name, oh_name, buff_size - 1);
+        }
+}
+
+static void yaffs_load_oh_from_name(struct yaffs_dev *dev, YCHAR * oh_name,
+				    const YCHAR * name)
+{
+#ifdef CONFIG_YAFFS_AUTO_UNICODE
+
+	int is_ascii;
+	YCHAR *w;
+
+	if (dev->param.auto_unicode) {
+
+		is_ascii = 1;
+		w = name;
+
+		/* Figure out if the name will fit in ascii character set */
+		while (is_ascii && *w) {
+			if ((*w) & 0xff00)
+				is_ascii = 0;
+			w++;
+		}
+
+		if (is_ascii) {
+			/* It is an ASCII name, so do a unicode to ascii conversion */
+			char *ascii_oh_name = (char *)oh_name;
+			int n = YAFFS_MAX_NAME_LENGTH - 1;
+			while (n > 0 && *name) {
+				*ascii_oh_name = *name;
+				name++;
+				ascii_oh_name++;
+				n--;
+			}
+		} else {
+			/* It is a unicode name, so save starting at the second YCHAR */
+			*oh_name = 0;
+			strncpy(oh_name + 1, name,
+				      YAFFS_MAX_NAME_LENGTH - 2);
+		}
+	} else {
+#else
+        {
+#endif
+		strncpy(oh_name, name, YAFFS_MAX_NAME_LENGTH - 1);
+        }
+
+}
+
+/* UpdateObjectHeader updates the header on NAND for an object.
+ * If name is not NULL, then that new name is used.
+ */
+int yaffs_update_oh(struct yaffs_obj *in, const YCHAR * name, int force,
+		    int is_shrink, int shadows, struct yaffs_xattr_mod *xmod)
+{
+
+	struct yaffs_block_info *bi;
+
+	struct yaffs_dev *dev = in->my_dev;
+
+	int prev_chunk_id;
+	int ret_val = 0;
+	int result = 0;
+
+	int new_chunk_id;
+	struct yaffs_ext_tags new_tags;
+	struct yaffs_ext_tags old_tags;
+	const YCHAR *alias = NULL;
+
+	u8 *buffer = NULL;
+	YCHAR old_name[YAFFS_MAX_NAME_LENGTH + 1];
+
+	struct yaffs_obj_hdr *oh = NULL;
+
+	strcpy(old_name, _Y("silly old name"));
+
+	if (!in->fake || in == dev->root_dir ||
+	    force || xmod) {
+
+		yaffs_check_gc(dev, 0);
+		yaffs_check_obj_details_loaded(in);
+
+		buffer = yaffs_get_temp_buffer(in->my_dev, __LINE__);
+		oh = (struct yaffs_obj_hdr *)buffer;
+
+		prev_chunk_id = in->hdr_chunk;
+
+		if (prev_chunk_id > 0) {
+			result = yaffs_rd_chunk_tags_nand(dev, prev_chunk_id,
+							  buffer, &old_tags);
+
+			yaffs_verify_oh(in, oh, &old_tags, 0);
+
+			memcpy(old_name, oh->name, sizeof(oh->name));
+			memset(buffer, 0xFF, sizeof(struct yaffs_obj_hdr));
+		} else {
+			memset(buffer, 0xFF, dev->data_bytes_per_chunk);
+                }
+
+		oh->type = in->variant_type;
+		oh->yst_mode = in->yst_mode;
+		oh->shadows_obj = oh->inband_shadowed_obj_id = shadows;
+
+		yaffs_load_attribs_oh(oh, in);
+
+		if (in->parent)
+			oh->parent_obj_id = in->parent->obj_id;
+		else
+			oh->parent_obj_id = 0;
+
+		if (name && *name) {
+			memset(oh->name, 0, sizeof(oh->name));
+			yaffs_load_oh_from_name(dev, oh->name, name);
+		} else if (prev_chunk_id > 0) {
+			memcpy(oh->name, old_name, sizeof(oh->name));
+		} else {
+			memset(oh->name, 0, sizeof(oh->name));
+                }
+
+		oh->is_shrink = is_shrink;
+
+		switch (in->variant_type) {
+		case YAFFS_OBJECT_TYPE_UNKNOWN:
+			/* Should not happen */
+			break;
+		case YAFFS_OBJECT_TYPE_FILE:
+			oh->file_size =
+			    (oh->parent_obj_id == YAFFS_OBJECTID_DELETED
+			     || oh->parent_obj_id ==
+			     YAFFS_OBJECTID_UNLINKED) ? 0 : in->
+			    variant.file_variant.file_size;
+			break;
+		case YAFFS_OBJECT_TYPE_HARDLINK:
+			oh->equiv_id = in->variant.hardlink_variant.equiv_id;
+			break;
+		case YAFFS_OBJECT_TYPE_SPECIAL:
+			/* Do nothing */
+			break;
+		case YAFFS_OBJECT_TYPE_DIRECTORY:
+			/* Do nothing */
+			break;
+		case YAFFS_OBJECT_TYPE_SYMLINK:
+			alias = in->variant.symlink_variant.alias;
+			if (!alias)
+				alias = _Y("no alias");
+			strncpy(oh->alias, alias, YAFFS_MAX_ALIAS_LENGTH);
+			oh->alias[YAFFS_MAX_ALIAS_LENGTH] = 0;
+			break;
+		}
+
+		/* process any xattrib modifications */
+		if (xmod)
+			yaffs_apply_xattrib_mod(in, (char *)buffer, xmod);
+
+		/* Tags */
+		yaffs_init_tags(&new_tags);
+		in->serial++;
+		new_tags.chunk_id = 0;
+		new_tags.obj_id = in->obj_id;
+		new_tags.serial_number = in->serial;
+
+		/* Add extra info for file header */
+
+		new_tags.extra_available = 1;
+		new_tags.extra_parent_id = oh->parent_obj_id;
+		new_tags.extra_length = oh->file_size;
+		new_tags.extra_is_shrink = oh->is_shrink;
+		new_tags.extra_equiv_id = oh->equiv_id;
+		new_tags.extra_shadows = (oh->shadows_obj > 0) ? 1 : 0;
+		new_tags.extra_obj_type = in->variant_type;
+
+		yaffs_verify_oh(in, oh, &new_tags, 1);
+
+		/* Create new chunk in NAND */
+		new_chunk_id =
+		    yaffs_write_new_chunk(dev, buffer, &new_tags,
+					  (prev_chunk_id > 0) ? 1 : 0);
+
+		if (new_chunk_id >= 0) {
+
+			in->hdr_chunk = new_chunk_id;
+
+			if (prev_chunk_id > 0) {
+				yaffs_chunk_del(dev, prev_chunk_id, 1,
+						__LINE__);
+			}
+
+			if (!yaffs_obj_cache_dirty(in))
+				in->dirty = 0;
+
+			/* If this was a shrink, then mark the block that the chunk lives on */
+			if (is_shrink) {
+				bi = yaffs_get_block_info(in->my_dev,
+							  new_chunk_id /
+							  in->my_dev->param.
+							  chunks_per_block);
+				bi->has_shrink_hdr = 1;
+			}
+
+		}
+
+		ret_val = new_chunk_id;
+
+	}
+
+	if (buffer)
+		yaffs_release_temp_buffer(dev, buffer, __LINE__);
+
+	return ret_val;
+}
+
+/*--------------------- File read/write ------------------------
+ * Read and write have very similar structures.
+ * In general the read/write has three parts to it
+ * An incomplete chunk to start with (if the read/write is not chunk-aligned)
+ * Some complete chunks
+ * An incomplete chunk to end off with
+ *
+ * Curve-balls: the first chunk might also be the last chunk.
+ */
+
+int yaffs_file_rd(struct yaffs_obj *in, u8 * buffer, loff_t offset, int n_bytes)
+{
+
+	int chunk;
+	u32 start;
+	int n_copy;
+	int n = n_bytes;
+	int n_done = 0;
+	struct yaffs_cache *cache;
+
+	struct yaffs_dev *dev;
+
+	dev = in->my_dev;
+
+	while (n > 0) {
+		/* chunk = offset / dev->data_bytes_per_chunk + 1; */
+		/* start = offset % dev->data_bytes_per_chunk; */
+		yaffs_addr_to_chunk(dev, offset, &chunk, &start);
+		chunk++;
+
+		/* OK now check for the curveball where the start and end are in
+		 * the same chunk.
+		 */
+		if ((start + n) < dev->data_bytes_per_chunk)
+			n_copy = n;
+		else
+			n_copy = dev->data_bytes_per_chunk - start;
+
+		cache = yaffs_find_chunk_cache(in, chunk);
+
+		/* If the chunk is already in the cache or it is less than a whole chunk
+		 * or we're using inband tags then use the cache (if there is caching)
+		 * else bypass the cache.
+		 */
+		if (cache || n_copy != dev->data_bytes_per_chunk
+		    || dev->param.inband_tags) {
+			if (dev->param.n_caches > 0) {
+
+				/* If we can't find the data in the cache, then load it up. */
+
+				if (!cache) {
+					cache =
+					    yaffs_grab_chunk_cache(in->my_dev);
+					cache->object = in;
+					cache->chunk_id = chunk;
+					cache->dirty = 0;
+					cache->locked = 0;
+					yaffs_rd_data_obj(in, chunk,
+							  cache->data);
+					cache->n_bytes = 0;
+				}
+
+				yaffs_use_cache(dev, cache, 0);
+
+				cache->locked = 1;
+
+				memcpy(buffer, &cache->data[start], n_copy);
+
+				cache->locked = 0;
+			} else {
+				/* Read into the local buffer then copy.. */
+
+				u8 *local_buffer =
+				    yaffs_get_temp_buffer(dev, __LINE__);
+				yaffs_rd_data_obj(in, chunk, local_buffer);
+
+				memcpy(buffer, &local_buffer[start], n_copy);
+
+				yaffs_release_temp_buffer(dev, local_buffer,
+							  __LINE__);
+			}
+
+		} else {
+
+			/* A full chunk. Read directly into the supplied buffer. */
+			yaffs_rd_data_obj(in, chunk, buffer);
+
+		}
+
+		n -= n_copy;
+		offset += n_copy;
+		buffer += n_copy;
+		n_done += n_copy;
+
+	}
+
+	return n_done;
+}
+
+int yaffs_do_file_wr(struct yaffs_obj *in, const u8 * buffer, loff_t offset,
+		     int n_bytes, int write_trhrough)
+{
+
+	int chunk;
+	u32 start;
+	int n_copy;
+	int n = n_bytes;
+	int n_done = 0;
+	int n_writeback;
+	int start_write = offset;
+	int chunk_written = 0;
+	u32 n_bytes_read;
+	u32 chunk_start;
+
+	struct yaffs_dev *dev;
+
+	dev = in->my_dev;
+
+	while (n > 0 && chunk_written >= 0) {
+		yaffs_addr_to_chunk(dev, offset, &chunk, &start);
+
+		if (chunk * dev->data_bytes_per_chunk + start != offset ||
+		    start >= dev->data_bytes_per_chunk) {
+			yaffs_trace(YAFFS_TRACE_ERROR,
+				"AddrToChunk of offset %d gives chunk %d start %d",
+				(int)offset, chunk, start);
+		}
+		chunk++;	/* File pos to chunk in file offset */
+
+		/* OK now check for the curveball where the start and end are in
+		 * the same chunk.
+		 */
+
+		if ((start + n) < dev->data_bytes_per_chunk) {
+			n_copy = n;
+
+			/* Now folks, to calculate how many bytes to write back....
+			 * If we're overwriting and not writing to then end of file then
+			 * we need to write back as much as was there before.
+			 */
+
+			chunk_start = ((chunk - 1) * dev->data_bytes_per_chunk);
+
+			if (chunk_start > in->variant.file_variant.file_size)
+				n_bytes_read = 0;	/* Past end of file */
+			else
+				n_bytes_read =
+				    in->variant.file_variant.file_size -
+				    chunk_start;
+
+			if (n_bytes_read > dev->data_bytes_per_chunk)
+				n_bytes_read = dev->data_bytes_per_chunk;
+
+			n_writeback =
+			    (n_bytes_read >
+			     (start + n)) ? n_bytes_read : (start + n);
+
+			if (n_writeback < 0
+			    || n_writeback > dev->data_bytes_per_chunk)
+				YBUG();
+
+		} else {
+			n_copy = dev->data_bytes_per_chunk - start;
+			n_writeback = dev->data_bytes_per_chunk;
+		}
+
+		if (n_copy != dev->data_bytes_per_chunk
+		    || dev->param.inband_tags) {
+			/* An incomplete start or end chunk (or maybe both start and end chunk),
+			 * or we're using inband tags, so we want to use the cache buffers.
+			 */
+			if (dev->param.n_caches > 0) {
+				struct yaffs_cache *cache;
+				/* If we can't find the data in the cache, then load the cache */
+				cache = yaffs_find_chunk_cache(in, chunk);
+
+				if (!cache
+				    && yaffs_check_alloc_available(dev, 1)) {
+					cache = yaffs_grab_chunk_cache(dev);
+					cache->object = in;
+					cache->chunk_id = chunk;
+					cache->dirty = 0;
+					cache->locked = 0;
+					yaffs_rd_data_obj(in, chunk,
+							  cache->data);
+				} else if (cache &&
+					   !cache->dirty &&
+					   !yaffs_check_alloc_available(dev,
+									1)) {
+					/* Drop the cache if it was a read cache item and
+					 * no space check has been made for it.
+					 */
+					cache = NULL;
+				}
+
+				if (cache) {
+					yaffs_use_cache(dev, cache, 1);
+					cache->locked = 1;
+
+					memcpy(&cache->data[start], buffer,
+					       n_copy);
+
+					cache->locked = 0;
+					cache->n_bytes = n_writeback;
+
+					if (write_trhrough) {
+						chunk_written =
+						    yaffs_wr_data_obj
+						    (cache->object,
+						     cache->chunk_id,
+						     cache->data,
+						     cache->n_bytes, 1);
+						cache->dirty = 0;
+					}
+
+				} else {
+					chunk_written = -1;	/* fail the write */
+				}
+			} else {
+				/* An incomplete start or end chunk (or maybe both start and end chunk)
+				 * Read into the local buffer then copy, then copy over and write back.
+				 */
+
+				u8 *local_buffer =
+				    yaffs_get_temp_buffer(dev, __LINE__);
+
+				yaffs_rd_data_obj(in, chunk, local_buffer);
+
+				memcpy(&local_buffer[start], buffer, n_copy);
+
+				chunk_written =
+				    yaffs_wr_data_obj(in, chunk,
+						      local_buffer,
+						      n_writeback, 0);
+
+				yaffs_release_temp_buffer(dev, local_buffer,
+							  __LINE__);
+
+			}
+
+		} else {
+			/* A full chunk. Write directly from the supplied buffer. */
+
+			chunk_written =
+			    yaffs_wr_data_obj(in, chunk, buffer,
+					      dev->data_bytes_per_chunk, 0);
+
+			/* Since we've overwritten the cached data, we better invalidate it. */
+			yaffs_invalidate_chunk_cache(in, chunk);
+		}
+
+		if (chunk_written >= 0) {
+			n -= n_copy;
+			offset += n_copy;
+			buffer += n_copy;
+			n_done += n_copy;
+		}
+
+	}
+
+	/* Update file object */
+
+	if ((start_write + n_done) > in->variant.file_variant.file_size)
+		in->variant.file_variant.file_size = (start_write + n_done);
+
+	in->dirty = 1;
+
+	return n_done;
+}
+
+int yaffs_wr_file(struct yaffs_obj *in, const u8 * buffer, loff_t offset,
+		  int n_bytes, int write_trhrough)
+{
+	yaffs2_handle_hole(in, offset);
+	return yaffs_do_file_wr(in, buffer, offset, n_bytes, write_trhrough);
+}
+
+/* ---------------------- File resizing stuff ------------------ */
+
+static void yaffs_prune_chunks(struct yaffs_obj *in, int new_size)
+{
+
+	struct yaffs_dev *dev = in->my_dev;
+	int old_size = in->variant.file_variant.file_size;
+
+	int last_del = 1 + (old_size - 1) / dev->data_bytes_per_chunk;
+
+	int start_del = 1 + (new_size + dev->data_bytes_per_chunk - 1) /
+	    dev->data_bytes_per_chunk;
+	int i;
+	int chunk_id;
+
+	/* Delete backwards so that we don't end up with holes if
+	 * power is lost part-way through the operation.
+	 */
+	for (i = last_del; i >= start_del; i--) {
+		/* NB this could be optimised somewhat,
+		 * eg. could retrieve the tags and write them without
+		 * using yaffs_chunk_del
+		 */
+
+		chunk_id = yaffs_find_del_file_chunk(in, i, NULL);
+		if (chunk_id > 0) {
+			if (chunk_id <
+			    (dev->internal_start_block *
+			     dev->param.chunks_per_block)
+			    || chunk_id >=
+			    ((dev->internal_end_block +
+			      1) * dev->param.chunks_per_block)) {
+				yaffs_trace(YAFFS_TRACE_ALWAYS,
+					"Found daft chunk_id %d for %d",
+					chunk_id, i);
+			} else {
+				in->n_data_chunks--;
+				yaffs_chunk_del(dev, chunk_id, 1, __LINE__);
+			}
+		}
+	}
+
+}
+
+void yaffs_resize_file_down(struct yaffs_obj *obj, loff_t new_size)
+{
+	int new_full;
+	u32 new_partial;
+	struct yaffs_dev *dev = obj->my_dev;
+
+	yaffs_addr_to_chunk(dev, new_size, &new_full, &new_partial);
+
+	yaffs_prune_chunks(obj, new_size);
+
+	if (new_partial != 0) {
+		int last_chunk = 1 + new_full;
+		u8 *local_buffer = yaffs_get_temp_buffer(dev, __LINE__);
+
+		/* Rewrite the last chunk with its new size and zero pad */
+		yaffs_rd_data_obj(obj, last_chunk, local_buffer);
+		memset(local_buffer + new_partial, 0,
+		       dev->data_bytes_per_chunk - new_partial);
+
+		yaffs_wr_data_obj(obj, last_chunk, local_buffer,
+				  new_partial, 1);
+
+		yaffs_release_temp_buffer(dev, local_buffer, __LINE__);
+	}
+
+	obj->variant.file_variant.file_size = new_size;
+
+	yaffs_prune_tree(dev, &obj->variant.file_variant);
+}
+
+int yaffs_resize_file(struct yaffs_obj *in, loff_t new_size)
+{
+	struct yaffs_dev *dev = in->my_dev;
+	int old_size = in->variant.file_variant.file_size;
+
+	yaffs_flush_file_cache(in);
+	yaffs_invalidate_whole_cache(in);
+
+	yaffs_check_gc(dev, 0);
+
+	if (in->variant_type != YAFFS_OBJECT_TYPE_FILE)
+		return YAFFS_FAIL;
+
+	if (new_size == old_size)
+		return YAFFS_OK;
+
+	if (new_size > old_size) {
+		yaffs2_handle_hole(in, new_size);
+		in->variant.file_variant.file_size = new_size;
+	} else {
+		/* new_size < old_size */
+		yaffs_resize_file_down(in, new_size);
+	}
+
+	/* Write a new object header to reflect the resize.
+	 * show we've shrunk the file, if need be
+	 * Do this only if the file is not in the deleted directories
+	 * and is not shadowed.
+	 */
+	if (in->parent &&
+	    !in->is_shadowed &&
+	    in->parent->obj_id != YAFFS_OBJECTID_UNLINKED &&
+	    in->parent->obj_id != YAFFS_OBJECTID_DELETED)
+		yaffs_update_oh(in, NULL, 0, 0, 0, NULL);
+
+	return YAFFS_OK;
+}
+
+int yaffs_flush_file(struct yaffs_obj *in, int update_time, int data_sync)
+{
+	int ret_val;
+	if (in->dirty) {
+		yaffs_flush_file_cache(in);
+		if (data_sync)	/* Only sync data */
+			ret_val = YAFFS_OK;
+		else {
+			if (update_time)
+				yaffs_load_current_time(in, 0, 0);
+
+			ret_val = (yaffs_update_oh(in, NULL, 0, 0, 0, NULL) >=
+				   0) ? YAFFS_OK : YAFFS_FAIL;
+		}
+	} else {
+		ret_val = YAFFS_OK;
+	}
+
+	return ret_val;
+
+}
+
+
+/* yaffs_del_file deletes the whole file data
+ * and the inode associated with the file.
+ * It does not delete the links associated with the file.
+ */
+static int yaffs_unlink_file_if_needed(struct yaffs_obj *in)
+{
+
+	int ret_val;
+	int del_now = 0;
+	struct yaffs_dev *dev = in->my_dev;
+
+	if (!in->my_inode)
+		del_now = 1;
+
+	if (del_now) {
+		ret_val =
+		    yaffs_change_obj_name(in, in->my_dev->del_dir,
+					  _Y("deleted"), 0, 0);
+		yaffs_trace(YAFFS_TRACE_TRACING,
+			"yaffs: immediate deletion of file %d",
+			in->obj_id);
+		in->deleted = 1;
+		in->my_dev->n_deleted_files++;
+		if (dev->param.disable_soft_del || dev->param.is_yaffs2)
+			yaffs_resize_file(in, 0);
+		yaffs_soft_del_file(in);
+	} else {
+		ret_val =
+		    yaffs_change_obj_name(in, in->my_dev->unlinked_dir,
+					  _Y("unlinked"), 0, 0);
+	}
+
+	return ret_val;
+}
+
+int yaffs_del_file(struct yaffs_obj *in)
+{
+	int ret_val = YAFFS_OK;
+	int deleted;		/* Need to cache value on stack if in is freed */
+	struct yaffs_dev *dev = in->my_dev;
+
+	if (dev->param.disable_soft_del || dev->param.is_yaffs2)
+		yaffs_resize_file(in, 0);
+
+	if (in->n_data_chunks > 0) {
+		/* Use soft deletion if there is data in the file.
+		 * That won't be the case if it has been resized to zero.
+		 */
+		if (!in->unlinked)
+			ret_val = yaffs_unlink_file_if_needed(in);
+
+		deleted = in->deleted;
+
+		if (ret_val == YAFFS_OK && in->unlinked && !in->deleted) {
+			in->deleted = 1;
+			deleted = 1;
+			in->my_dev->n_deleted_files++;
+			yaffs_soft_del_file(in);
+		}
+		return deleted ? YAFFS_OK : YAFFS_FAIL;
+	} else {
+		/* The file has no data chunks so we toss it immediately */
+		yaffs_free_tnode(in->my_dev, in->variant.file_variant.top);
+		in->variant.file_variant.top = NULL;
+		yaffs_generic_obj_del(in);
+
+		return YAFFS_OK;
+	}
+}
+
+int yaffs_is_non_empty_dir(struct yaffs_obj *obj)
+{
+	return (obj &&
+	        obj->variant_type == YAFFS_OBJECT_TYPE_DIRECTORY) &&
+	        !(list_empty(&obj->variant.dir_variant.children));
+}
+
+static int yaffs_del_dir(struct yaffs_obj *obj)
+{
+	/* First check that the directory is empty. */
+	if (yaffs_is_non_empty_dir(obj))
+		return YAFFS_FAIL;
+
+	return yaffs_generic_obj_del(obj);
+}
+
+static int yaffs_del_symlink(struct yaffs_obj *in)
+{
+	if (in->variant.symlink_variant.alias)
+		kfree(in->variant.symlink_variant.alias);
+	in->variant.symlink_variant.alias = NULL;
+
+	return yaffs_generic_obj_del(in);
+}
+
+static int yaffs_del_link(struct yaffs_obj *in)
+{
+	/* remove this hardlink from the list assocaited with the equivalent
+	 * object
+	 */
+	list_del_init(&in->hard_links);
+	return yaffs_generic_obj_del(in);
+}
+
+int yaffs_del_obj(struct yaffs_obj *obj)
+{
+	int ret_val = -1;
+	switch (obj->variant_type) {
+	case YAFFS_OBJECT_TYPE_FILE:
+		ret_val = yaffs_del_file(obj);
+		break;
+	case YAFFS_OBJECT_TYPE_DIRECTORY:
+		if (!list_empty(&obj->variant.dir_variant.dirty)) {
+			yaffs_trace(YAFFS_TRACE_BACKGROUND,
+				"Remove object %d from dirty directories",
+				obj->obj_id);
+			list_del_init(&obj->variant.dir_variant.dirty);
+		}
+		return yaffs_del_dir(obj);
+		break;
+	case YAFFS_OBJECT_TYPE_SYMLINK:
+		ret_val = yaffs_del_symlink(obj);
+		break;
+	case YAFFS_OBJECT_TYPE_HARDLINK:
+		ret_val = yaffs_del_link(obj);
+		break;
+	case YAFFS_OBJECT_TYPE_SPECIAL:
+		ret_val = yaffs_generic_obj_del(obj);
+		break;
+	case YAFFS_OBJECT_TYPE_UNKNOWN:
+		ret_val = 0;
+		break;		/* should not happen. */
+	}
+
+	return ret_val;
+}
+
+static int yaffs_unlink_worker(struct yaffs_obj *obj)
+{
+
+	int del_now = 0;
+
+	if (!obj->my_inode)
+		del_now = 1;
+
+	if (obj)
+		yaffs_update_parent(obj->parent);
+
+	if (obj->variant_type == YAFFS_OBJECT_TYPE_HARDLINK) {
+		return yaffs_del_link(obj);
+	} else if (!list_empty(&obj->hard_links)) {
+		/* Curve ball: We're unlinking an object that has a hardlink.
+		 *
+		 * This problem arises because we are not strictly following
+		 * The Linux link/inode model.
+		 *
+		 * We can't really delete the object.
+		 * Instead, we do the following:
+		 * - Select a hardlink.
+		 * - Unhook it from the hard links
+		 * - Move it from its parent directory (so that the rename can work)
+		 * - Rename the object to the hardlink's name.
+		 * - Delete the hardlink
+		 */
+
+		struct yaffs_obj *hl;
+		struct yaffs_obj *parent;
+		int ret_val;
+		YCHAR name[YAFFS_MAX_NAME_LENGTH + 1];
+
+		hl = list_entry(obj->hard_links.next, struct yaffs_obj,
+				hard_links);
+
+		yaffs_get_obj_name(hl, name, YAFFS_MAX_NAME_LENGTH + 1);
+		parent = hl->parent;
+
+		list_del_init(&hl->hard_links);
+
+		yaffs_add_obj_to_dir(obj->my_dev->unlinked_dir, hl);
+
+		ret_val = yaffs_change_obj_name(obj, parent, name, 0, 0);
+
+		if (ret_val == YAFFS_OK)
+			ret_val = yaffs_generic_obj_del(hl);
+
+		return ret_val;
+
+	} else if (del_now) {
+		switch (obj->variant_type) {
+		case YAFFS_OBJECT_TYPE_FILE:
+			return yaffs_del_file(obj);
+			break;
+		case YAFFS_OBJECT_TYPE_DIRECTORY:
+			list_del_init(&obj->variant.dir_variant.dirty);
+			return yaffs_del_dir(obj);
+			break;
+		case YAFFS_OBJECT_TYPE_SYMLINK:
+			return yaffs_del_symlink(obj);
+			break;
+		case YAFFS_OBJECT_TYPE_SPECIAL:
+			return yaffs_generic_obj_del(obj);
+			break;
+		case YAFFS_OBJECT_TYPE_HARDLINK:
+		case YAFFS_OBJECT_TYPE_UNKNOWN:
+		default:
+			return YAFFS_FAIL;
+		}
+	} else if (yaffs_is_non_empty_dir(obj)) {
+		return YAFFS_FAIL;
+	} else {
+		return yaffs_change_obj_name(obj, obj->my_dev->unlinked_dir,
+					     _Y("unlinked"), 0, 0);
+        }
+}
+
+static int yaffs_unlink_obj(struct yaffs_obj *obj)
+{
+
+	if (obj && obj->unlink_allowed)
+		return yaffs_unlink_worker(obj);
+
+	return YAFFS_FAIL;
+
+}
+
+int yaffs_unlinker(struct yaffs_obj *dir, const YCHAR * name)
+{
+	struct yaffs_obj *obj;
+
+	obj = yaffs_find_by_name(dir, name);
+	return yaffs_unlink_obj(obj);
+}
+
+/* Note:
+ * If old_name is NULL then we take old_dir as the object to be renamed.
+ */
+int yaffs_rename_obj(struct yaffs_obj *old_dir, const YCHAR * old_name,
+		     struct yaffs_obj *new_dir, const YCHAR * new_name)
+{
+	struct yaffs_obj *obj = NULL;
+	struct yaffs_obj *existing_target = NULL;
+	int force = 0;
+	int result;
+	struct yaffs_dev *dev;
+
+	if (!old_dir || old_dir->variant_type != YAFFS_OBJECT_TYPE_DIRECTORY)
+		YBUG();
+	if (!new_dir || new_dir->variant_type != YAFFS_OBJECT_TYPE_DIRECTORY)
+		YBUG();
+
+	dev = old_dir->my_dev;
+
+#ifdef CONFIG_YAFFS_CASE_INSENSITIVE
+	/* Special case for case insemsitive systems.
+	 * While look-up is case insensitive, the name isn't.
+	 * Therefore we might want to change x.txt to X.txt
+	 */
+	if (old_dir == new_dir && 
+		old_name && new_name && 
+		strcmp(old_name, new_name) == 0)
+		force = 1;
+#endif
+
+	if (strnlen(new_name, YAFFS_MAX_NAME_LENGTH + 1) >
+	    YAFFS_MAX_NAME_LENGTH)
+		/* ENAMETOOLONG */
+		return YAFFS_FAIL;
+
+	if(old_name)
+		obj = yaffs_find_by_name(old_dir, old_name);
+	else{
+		obj = old_dir;
+		old_dir = obj->parent;
+	}
+
+
+	if (obj && obj->rename_allowed) {
+
+		/* Now do the handling for an existing target, if there is one */
+
+		existing_target = yaffs_find_by_name(new_dir, new_name);
+		if (yaffs_is_non_empty_dir(existing_target)){
+			return YAFFS_FAIL;	/* ENOTEMPTY */
+		} else if (existing_target && existing_target != obj) {
+			/* Nuke the target first, using shadowing,
+			 * but only if it isn't the same object.
+			 *
+			 * Note we must disable gc otherwise it can mess up the shadowing.
+			 *
+			 */
+			dev->gc_disable = 1;
+			yaffs_change_obj_name(obj, new_dir, new_name, force,
+					      existing_target->obj_id);
+			existing_target->is_shadowed = 1;
+			yaffs_unlink_obj(existing_target);
+			dev->gc_disable = 0;
+		}
+
+		result = yaffs_change_obj_name(obj, new_dir, new_name, 1, 0);
+
+		yaffs_update_parent(old_dir);
+		if (new_dir != old_dir)
+			yaffs_update_parent(new_dir);
+
+		return result;
+	}
+	return YAFFS_FAIL;
+}
+
+/*----------------------- Initialisation Scanning ---------------------- */
+
+void yaffs_handle_shadowed_obj(struct yaffs_dev *dev, int obj_id,
+			       int backward_scanning)
+{
+	struct yaffs_obj *obj;
+
+	if (!backward_scanning) {
+		/* Handle YAFFS1 forward scanning case
+		 * For YAFFS1 we always do the deletion
+		 */
+
+	} else {
+		/* Handle YAFFS2 case (backward scanning)
+		 * If the shadowed object exists then ignore.
+		 */
+		obj = yaffs_find_by_number(dev, obj_id);
+		if (obj)
+			return;
+	}
+
+	/* Let's create it (if it does not exist) assuming it is a file so that it can do shrinking etc.
+	 * We put it in unlinked dir to be cleaned up after the scanning
+	 */
+	obj =
+	    yaffs_find_or_create_by_number(dev, obj_id, YAFFS_OBJECT_TYPE_FILE);
+	if (!obj)
+		return;
+	obj->is_shadowed = 1;
+	yaffs_add_obj_to_dir(dev->unlinked_dir, obj);
+	obj->variant.file_variant.shrink_size = 0;
+	obj->valid = 1;		/* So that we don't read any other info for this file */
+
+}
+
+void yaffs_link_fixup(struct yaffs_dev *dev, struct yaffs_obj *hard_list)
+{
+	struct yaffs_obj *hl;
+	struct yaffs_obj *in;
+
+	while (hard_list) {
+		hl = hard_list;
+		hard_list = (struct yaffs_obj *)(hard_list->hard_links.next);
+
+		in = yaffs_find_by_number(dev,
+					  hl->variant.
+					  hardlink_variant.equiv_id);
+
+		if (in) {
+			/* Add the hardlink pointers */
+			hl->variant.hardlink_variant.equiv_obj = in;
+			list_add(&hl->hard_links, &in->hard_links);
+		} else {
+			/* Todo Need to report/handle this better.
+			 * Got a problem... hardlink to a non-existant object
+			 */
+			hl->variant.hardlink_variant.equiv_obj = NULL;
+			INIT_LIST_HEAD(&hl->hard_links);
+
+		}
+	}
+}
+
+static void yaffs_strip_deleted_objs(struct yaffs_dev *dev)
+{
+	/*
+	 *  Sort out state of unlinked and deleted objects after scanning.
+	 */
+	struct list_head *i;
+	struct list_head *n;
+	struct yaffs_obj *l;
+
+	if (dev->read_only)
+		return;
+
+	/* Soft delete all the unlinked files */
+	list_for_each_safe(i, n,
+			   &dev->unlinked_dir->variant.dir_variant.children) {
+		if (i) {
+			l = list_entry(i, struct yaffs_obj, siblings);
+			yaffs_del_obj(l);
+		}
+	}
+
+	list_for_each_safe(i, n, &dev->del_dir->variant.dir_variant.children) {
+		if (i) {
+			l = list_entry(i, struct yaffs_obj, siblings);
+			yaffs_del_obj(l);
+		}
+	}
+
+}
+
+/*
+ * This code iterates through all the objects making sure that they are rooted.
+ * Any unrooted objects are re-rooted in lost+found.
+ * An object needs to be in one of:
+ * - Directly under deleted, unlinked
+ * - Directly or indirectly under root.
+ *
+ * Note:
+ *  This code assumes that we don't ever change the current relationships between
+ *  directories:
+ *   root_dir->parent == unlinked_dir->parent == del_dir->parent == NULL
+ *   lost-n-found->parent == root_dir
+ *
+ * This fixes the problem where directories might have inadvertently been deleted
+ * leaving the object "hanging" without being rooted in the directory tree.
+ */
+
+static int yaffs_has_null_parent(struct yaffs_dev *dev, struct yaffs_obj *obj)
+{
+	return (obj == dev->del_dir ||
+		obj == dev->unlinked_dir || obj == dev->root_dir);
+}
+
+static void yaffs_fix_hanging_objs(struct yaffs_dev *dev)
+{
+	struct yaffs_obj *obj;
+	struct yaffs_obj *parent;
+	int i;
+	struct list_head *lh;
+	struct list_head *n;
+	int depth_limit;
+	int hanging;
+
+	if (dev->read_only)
+		return;
+
+	/* Iterate through the objects in each hash entry,
+	 * looking at each object.
+	 * Make sure it is rooted.
+	 */
+
+	for (i = 0; i < YAFFS_NOBJECT_BUCKETS; i++) {
+		list_for_each_safe(lh, n, &dev->obj_bucket[i].list) {
+			if (lh) {
+				obj =
+				    list_entry(lh, struct yaffs_obj, hash_link);
+				parent = obj->parent;
+
+				if (yaffs_has_null_parent(dev, obj)) {
+					/* These directories are not hanging */
+					hanging = 0;
+				} else if (!parent
+					   || parent->variant_type !=
+					   YAFFS_OBJECT_TYPE_DIRECTORY) {
+					hanging = 1;
+				} else if (yaffs_has_null_parent(dev, parent)) {
+					hanging = 0;
+				} else {
+					/*
+					 * Need to follow the parent chain to see if it is hanging.
+					 */
+					hanging = 0;
+					depth_limit = 100;
+
+					while (parent != dev->root_dir &&
+					       parent->parent &&
+					       parent->parent->variant_type ==
+					       YAFFS_OBJECT_TYPE_DIRECTORY
+					       && depth_limit > 0) {
+						parent = parent->parent;
+						depth_limit--;
+					}
+					if (parent != dev->root_dir)
+						hanging = 1;
+				}
+				if (hanging) {
+					yaffs_trace(YAFFS_TRACE_SCAN,
+						"Hanging object %d moved to lost and found",
+						obj->obj_id);
+					yaffs_add_obj_to_dir(dev->lost_n_found,
+							     obj);
+				}
+			}
+		}
+	}
+}
+
+/*
+ * Delete directory contents for cleaning up lost and found.
+ */
+static void yaffs_del_dir_contents(struct yaffs_obj *dir)
+{
+	struct yaffs_obj *obj;
+	struct list_head *lh;
+	struct list_head *n;
+
+	if (dir->variant_type != YAFFS_OBJECT_TYPE_DIRECTORY)
+		YBUG();
+
+	list_for_each_safe(lh, n, &dir->variant.dir_variant.children) {
+		if (lh) {
+			obj = list_entry(lh, struct yaffs_obj, siblings);
+			if (obj->variant_type == YAFFS_OBJECT_TYPE_DIRECTORY)
+				yaffs_del_dir_contents(obj);
+
+			yaffs_trace(YAFFS_TRACE_SCAN,
+				"Deleting lost_found object %d",
+				obj->obj_id);
+
+			/* Need to use UnlinkObject since Delete would not handle
+			 * hardlinked objects correctly.
+			 */
+			yaffs_unlink_obj(obj);
+		}
+	}
+
+}
+
+static void yaffs_empty_l_n_f(struct yaffs_dev *dev)
+{
+	yaffs_del_dir_contents(dev->lost_n_found);
+}
+
+
+struct yaffs_obj *yaffs_find_by_name(struct yaffs_obj *directory,
+				     const YCHAR * name)
+{
+	int sum;
+
+	struct list_head *i;
+	YCHAR buffer[YAFFS_MAX_NAME_LENGTH + 1];
+
+	struct yaffs_obj *l;
+
+	if (!name)
+		return NULL;
+
+	if (!directory) {
+		yaffs_trace(YAFFS_TRACE_ALWAYS,
+			"tragedy: yaffs_find_by_name: null pointer directory"
+			);
+		YBUG();
+		return NULL;
+	}
+	if (directory->variant_type != YAFFS_OBJECT_TYPE_DIRECTORY) {
+		yaffs_trace(YAFFS_TRACE_ALWAYS,
+			"tragedy: yaffs_find_by_name: non-directory"
+			);
+		YBUG();
+	}
+
+	sum = yaffs_calc_name_sum(name);
+
+	list_for_each(i, &directory->variant.dir_variant.children) {
+		if (i) {
+			l = list_entry(i, struct yaffs_obj, siblings);
+
+			if (l->parent != directory)
+				YBUG();
+
+			yaffs_check_obj_details_loaded(l);
+
+			/* Special case for lost-n-found */
+			if (l->obj_id == YAFFS_OBJECTID_LOSTNFOUND) {
+				if (!strcmp(name, YAFFS_LOSTNFOUND_NAME))
+					return l;
+			} else if (l->sum == sum
+				   || l->hdr_chunk <= 0) {
+				/* LostnFound chunk called Objxxx
+				 * Do a real check
+				 */
+				yaffs_get_obj_name(l, buffer,
+						   YAFFS_MAX_NAME_LENGTH + 1);
+				if (strncmp
+				    (name, buffer, YAFFS_MAX_NAME_LENGTH) == 0)
+					return l;
+			}
+		}
+	}
+
+	return NULL;
+}
+
+/* GetEquivalentObject dereferences any hard links to get to the
+ * actual object.
+ */
+
+struct yaffs_obj *yaffs_get_equivalent_obj(struct yaffs_obj *obj)
+{
+	if (obj && obj->variant_type == YAFFS_OBJECT_TYPE_HARDLINK) {
+		/* We want the object id of the equivalent object, not this one */
+		obj = obj->variant.hardlink_variant.equiv_obj;
+		yaffs_check_obj_details_loaded(obj);
+	}
+	return obj;
+}
+
+/*
+ *  A note or two on object names.
+ *  * If the object name is missing, we then make one up in the form objnnn
+ *
+ *  * ASCII names are stored in the object header's name field from byte zero
+ *  * Unicode names are historically stored starting from byte zero.
+ *
+ * Then there are automatic Unicode names...
+ * The purpose of these is to save names in a way that can be read as
+ * ASCII or Unicode names as appropriate, thus allowing a Unicode and ASCII
+ * system to share files.
+ *
+ * These automatic unicode are stored slightly differently...
+ *  - If the name can fit in the ASCII character space then they are saved as 
+ *    ascii names as per above.
+ *  - If the name needs Unicode then the name is saved in Unicode
+ *    starting at oh->name[1].
+
+ */
+static void yaffs_fix_null_name(struct yaffs_obj *obj, YCHAR * name,
+				int buffer_size)
+{
+	/* Create an object name if we could not find one. */
+	if (strnlen(name, YAFFS_MAX_NAME_LENGTH) == 0) {
+		YCHAR local_name[20];
+		YCHAR num_string[20];
+		YCHAR *x = &num_string[19];
+		unsigned v = obj->obj_id;
+		num_string[19] = 0;
+		while (v > 0) {
+			x--;
+			*x = '0' + (v % 10);
+			v /= 10;
+		}
+		/* make up a name */
+		strcpy(local_name, YAFFS_LOSTNFOUND_PREFIX);
+		strcat(local_name, x);
+		strncpy(name, local_name, buffer_size - 1);
+	}
+}
+
+int yaffs_get_obj_name(struct yaffs_obj *obj, YCHAR * name, int buffer_size)
+{
+	memset(name, 0, buffer_size * sizeof(YCHAR));
+
+	yaffs_check_obj_details_loaded(obj);
+
+	if (obj->obj_id == YAFFS_OBJECTID_LOSTNFOUND) {
+		strncpy(name, YAFFS_LOSTNFOUND_NAME, buffer_size - 1);
+	}
+#ifndef CONFIG_YAFFS_NO_SHORT_NAMES
+	else if (obj->short_name[0]) {
+		strcpy(name, obj->short_name);
+	}
+#endif
+	else if (obj->hdr_chunk > 0) {
+		int result;
+		u8 *buffer = yaffs_get_temp_buffer(obj->my_dev, __LINE__);
+
+		struct yaffs_obj_hdr *oh = (struct yaffs_obj_hdr *)buffer;
+
+		memset(buffer, 0, obj->my_dev->data_bytes_per_chunk);
+
+		if (obj->hdr_chunk > 0) {
+			result = yaffs_rd_chunk_tags_nand(obj->my_dev,
+							  obj->hdr_chunk,
+							  buffer, NULL);
+		}
+		yaffs_load_name_from_oh(obj->my_dev, name, oh->name,
+					buffer_size);
+
+		yaffs_release_temp_buffer(obj->my_dev, buffer, __LINE__);
+	}
+
+	yaffs_fix_null_name(obj, name, buffer_size);
+
+	return strnlen(name, YAFFS_MAX_NAME_LENGTH);
+}
+
+int yaffs_get_obj_length(struct yaffs_obj *obj)
+{
+	/* Dereference any hard linking */
+	obj = yaffs_get_equivalent_obj(obj);
+
+	if (obj->variant_type == YAFFS_OBJECT_TYPE_FILE)
+		return obj->variant.file_variant.file_size;
+	if (obj->variant_type == YAFFS_OBJECT_TYPE_SYMLINK) {
+		if (!obj->variant.symlink_variant.alias)
+			return 0;
+		return strnlen(obj->variant.symlink_variant.alias,
+				     YAFFS_MAX_ALIAS_LENGTH);
+	} else {
+		/* Only a directory should drop through to here */
+		return obj->my_dev->data_bytes_per_chunk;
+	}
+}
+
+int yaffs_get_obj_link_count(struct yaffs_obj *obj)
+{
+	int count = 0;
+	struct list_head *i;
+
+	if (!obj->unlinked)
+		count++;	/* the object itself */
+
+	list_for_each(i, &obj->hard_links)
+	    count++;		/* add the hard links; */
+
+	return count;
+}
+
+int yaffs_get_obj_inode(struct yaffs_obj *obj)
+{
+	obj = yaffs_get_equivalent_obj(obj);
+
+	return obj->obj_id;
+}
+
+unsigned yaffs_get_obj_type(struct yaffs_obj *obj)
+{
+	obj = yaffs_get_equivalent_obj(obj);
+
+	switch (obj->variant_type) {
+	case YAFFS_OBJECT_TYPE_FILE:
+		return DT_REG;
+		break;
+	case YAFFS_OBJECT_TYPE_DIRECTORY:
+		return DT_DIR;
+		break;
+	case YAFFS_OBJECT_TYPE_SYMLINK:
+		return DT_LNK;
+		break;
+	case YAFFS_OBJECT_TYPE_HARDLINK:
+		return DT_REG;
+		break;
+	case YAFFS_OBJECT_TYPE_SPECIAL:
+		if (S_ISFIFO(obj->yst_mode))
+			return DT_FIFO;
+		if (S_ISCHR(obj->yst_mode))
+			return DT_CHR;
+		if (S_ISBLK(obj->yst_mode))
+			return DT_BLK;
+		if (S_ISSOCK(obj->yst_mode))
+			return DT_SOCK;
+	default:
+		return DT_REG;
+		break;
+	}
+}
+
+YCHAR *yaffs_get_symlink_alias(struct yaffs_obj *obj)
+{
+	obj = yaffs_get_equivalent_obj(obj);
+	if (obj->variant_type == YAFFS_OBJECT_TYPE_SYMLINK)
+		return yaffs_clone_str(obj->variant.symlink_variant.alias);
+	else
+		return yaffs_clone_str(_Y(""));
+}
+
+/*--------------------------- Initialisation code -------------------------- */
+
+static int yaffs_check_dev_fns(const struct yaffs_dev *dev)
+{
+
+	/* Common functions, gotta have */
+	if (!dev->param.erase_fn || !dev->param.initialise_flash_fn)
+		return 0;
+
+#ifdef CONFIG_YAFFS_YAFFS2
+
+	/* Can use the "with tags" style interface for yaffs1 or yaffs2 */
+	if (dev->param.write_chunk_tags_fn &&
+	    dev->param.read_chunk_tags_fn &&
+	    !dev->param.write_chunk_fn &&
+	    !dev->param.read_chunk_fn &&
+	    dev->param.bad_block_fn && dev->param.query_block_fn)
+		return 1;
+#endif
+
+	/* Can use the "spare" style interface for yaffs1 */
+	if (!dev->param.is_yaffs2 &&
+	    !dev->param.write_chunk_tags_fn &&
+	    !dev->param.read_chunk_tags_fn &&
+	    dev->param.write_chunk_fn &&
+	    dev->param.read_chunk_fn &&
+	    !dev->param.bad_block_fn && !dev->param.query_block_fn)
+		return 1;
+
+	return 0;		/* bad */
+}
+
+static int yaffs_create_initial_dir(struct yaffs_dev *dev)
+{
+	/* Initialise the unlinked, deleted, root and lost and found directories */
+
+	dev->lost_n_found = dev->root_dir = NULL;
+	dev->unlinked_dir = dev->del_dir = NULL;
+
+	dev->unlinked_dir =
+	    yaffs_create_fake_dir(dev, YAFFS_OBJECTID_UNLINKED, S_IFDIR);
+
+	dev->del_dir =
+	    yaffs_create_fake_dir(dev, YAFFS_OBJECTID_DELETED, S_IFDIR);
+
+	dev->root_dir =
+	    yaffs_create_fake_dir(dev, YAFFS_OBJECTID_ROOT,
+				  YAFFS_ROOT_MODE | S_IFDIR);
+	dev->lost_n_found =
+	    yaffs_create_fake_dir(dev, YAFFS_OBJECTID_LOSTNFOUND,
+				  YAFFS_LOSTNFOUND_MODE | S_IFDIR);
+
+	if (dev->lost_n_found && dev->root_dir && dev->unlinked_dir
+	    && dev->del_dir) {
+		yaffs_add_obj_to_dir(dev->root_dir, dev->lost_n_found);
+		return YAFFS_OK;
+	}
+
+	return YAFFS_FAIL;
+}
+
+int yaffs_guts_initialise(struct yaffs_dev *dev)
+{
+	int init_failed = 0;
+	unsigned x;
+	int bits;
+
+	yaffs_trace(YAFFS_TRACE_TRACING, "yaffs: yaffs_guts_initialise()" );
+
+	/* Check stuff that must be set */
+
+	if (!dev) {
+		yaffs_trace(YAFFS_TRACE_ALWAYS,
+			"yaffs: Need a device"
+			);
+		return YAFFS_FAIL;
+	}
+
+	dev->internal_start_block = dev->param.start_block;
+	dev->internal_end_block = dev->param.end_block;
+	dev->block_offset = 0;
+	dev->chunk_offset = 0;
+	dev->n_free_chunks = 0;
+
+	dev->gc_block = 0;
+
+	if (dev->param.start_block == 0) {
+		dev->internal_start_block = dev->param.start_block + 1;
+		dev->internal_end_block = dev->param.end_block + 1;
+		dev->block_offset = 1;
+		dev->chunk_offset = dev->param.chunks_per_block;
+	}
+
+	/* Check geometry parameters. */
+
+	if ((!dev->param.inband_tags && dev->param.is_yaffs2 &&
+		dev->param.total_bytes_per_chunk < 1024) ||
+		(!dev->param.is_yaffs2 &&
+			dev->param.total_bytes_per_chunk < 512) ||
+		(dev->param.inband_tags && !dev->param.is_yaffs2) ||
+		 dev->param.chunks_per_block < 2 ||
+		 dev->param.n_reserved_blocks < 2 ||
+		dev->internal_start_block <= 0 || 
+		dev->internal_end_block <= 0 || 
+		dev->internal_end_block <= 
+		(dev->internal_start_block + dev->param.n_reserved_blocks + 2)
+		) {
+		/* otherwise it is too small */
+		yaffs_trace(YAFFS_TRACE_ALWAYS,
+			"NAND geometry problems: chunk size %d, type is yaffs%s, inband_tags %d ",
+			dev->param.total_bytes_per_chunk,
+			dev->param.is_yaffs2 ? "2" : "",
+			dev->param.inband_tags);
+		return YAFFS_FAIL;
+	}
+
+	if (yaffs_init_nand(dev) != YAFFS_OK) {
+		yaffs_trace(YAFFS_TRACE_ALWAYS, "InitialiseNAND failed");
+		return YAFFS_FAIL;
+	}
+
+	/* Sort out space for inband tags, if required */
+	if (dev->param.inband_tags)
+		dev->data_bytes_per_chunk =
+		    dev->param.total_bytes_per_chunk -
+		    sizeof(struct yaffs_packed_tags2_tags_only);
+	else
+		dev->data_bytes_per_chunk = dev->param.total_bytes_per_chunk;
+
+	/* Got the right mix of functions? */
+	if (!yaffs_check_dev_fns(dev)) {
+		/* Function missing */
+		yaffs_trace(YAFFS_TRACE_ALWAYS,
+			"device function(s) missing or wrong");
+
+		return YAFFS_FAIL;
+	}
+
+	if (dev->is_mounted) {
+		yaffs_trace(YAFFS_TRACE_ALWAYS, "device already mounted");
+		return YAFFS_FAIL;
+	}
+
+	/* Finished with most checks. One or two more checks happen later on too. */
+
+	dev->is_mounted = 1;
+
+	/* OK now calculate a few things for the device */
+
+	/*
+	 *  Calculate all the chunk size manipulation numbers:
+	 */
+	x = dev->data_bytes_per_chunk;
+	/* We always use dev->chunk_shift and dev->chunk_div */
+	dev->chunk_shift = calc_shifts(x);
+	x >>= dev->chunk_shift;
+	dev->chunk_div = x;
+	/* We only use chunk mask if chunk_div is 1 */
+	dev->chunk_mask = (1 << dev->chunk_shift) - 1;
+
+	/*
+	 * Calculate chunk_grp_bits.
+	 * We need to find the next power of 2 > than internal_end_block
+	 */
+
+	x = dev->param.chunks_per_block * (dev->internal_end_block + 1);
+
+	bits = calc_shifts_ceiling(x);
+
+	/* Set up tnode width if wide tnodes are enabled. */
+	if (!dev->param.wide_tnodes_disabled) {
+		/* bits must be even so that we end up with 32-bit words */
+		if (bits & 1)
+			bits++;
+		if (bits < 16)
+			dev->tnode_width = 16;
+		else
+			dev->tnode_width = bits;
+	} else {
+		dev->tnode_width = 16;
+        }
+
+	dev->tnode_mask = (1 << dev->tnode_width) - 1;
+
+	/* Level0 Tnodes are 16 bits or wider (if wide tnodes are enabled),
+	 * so if the bitwidth of the
+	 * chunk range we're using is greater than 16 we need
+	 * to figure out chunk shift and chunk_grp_size
+	 */
+
+	if (bits <= dev->tnode_width)
+		dev->chunk_grp_bits = 0;
+	else
+		dev->chunk_grp_bits = bits - dev->tnode_width;
+
+	dev->tnode_size = (dev->tnode_width * YAFFS_NTNODES_LEVEL0) / 8;
+	if (dev->tnode_size < sizeof(struct yaffs_tnode))
+		dev->tnode_size = sizeof(struct yaffs_tnode);
+
+	dev->chunk_grp_size = 1 << dev->chunk_grp_bits;
+
+	if (dev->param.chunks_per_block < dev->chunk_grp_size) {
+		/* We have a problem because the soft delete won't work if
+		 * the chunk group size > chunks per block.
+		 * This can be remedied by using larger "virtual blocks".
+		 */
+		yaffs_trace(YAFFS_TRACE_ALWAYS, "chunk group too large");
+
+		return YAFFS_FAIL;
+	}
+
+	/* OK, we've finished verifying the device, lets continue with initialisation */
+
+	/* More device initialisation */
+	dev->all_gcs = 0;
+	dev->passive_gc_count = 0;
+	dev->oldest_dirty_gc_count = 0;
+	dev->bg_gcs = 0;
+	dev->gc_block_finder = 0;
+	dev->buffered_block = -1;
+	dev->doing_buffered_block_rewrite = 0;
+	dev->n_deleted_files = 0;
+	dev->n_bg_deletions = 0;
+	dev->n_unlinked_files = 0;
+	dev->n_ecc_fixed = 0;
+	dev->n_ecc_unfixed = 0;
+	dev->n_tags_ecc_fixed = 0;
+	dev->n_tags_ecc_unfixed = 0;
+	dev->n_erase_failures = 0;
+	dev->n_erased_blocks = 0;
+	dev->gc_disable = 0;
+	dev->has_pending_prioritised_gc = 1;	/* Assume the worst for now, will get fixed on first GC */
+	INIT_LIST_HEAD(&dev->dirty_dirs);
+	dev->oldest_dirty_seq = 0;
+	dev->oldest_dirty_block = 0;
+
+	/* Initialise temporary buffers and caches. */
+	if (!yaffs_init_tmp_buffers(dev))
+		init_failed = 1;
+
+	dev->cache = NULL;
+	dev->gc_cleanup_list = NULL;
+
+	if (!init_failed && dev->param.n_caches > 0) {
+		int i;
+		void *buf;
+		int cache_bytes =
+		    dev->param.n_caches * sizeof(struct yaffs_cache);
+
+		if (dev->param.n_caches > YAFFS_MAX_SHORT_OP_CACHES)
+			dev->param.n_caches = YAFFS_MAX_SHORT_OP_CACHES;
+
+		dev->cache = kmalloc(cache_bytes, GFP_NOFS);
+
+		buf = (u8 *) dev->cache;
+
+		if (dev->cache)
+			memset(dev->cache, 0, cache_bytes);
+
+		for (i = 0; i < dev->param.n_caches && buf; i++) {
+			dev->cache[i].object = NULL;
+			dev->cache[i].last_use = 0;
+			dev->cache[i].dirty = 0;
+			dev->cache[i].data = buf =
+			    kmalloc(dev->param.total_bytes_per_chunk, GFP_NOFS);
+		}
+		if (!buf)
+			init_failed = 1;
+
+		dev->cache_last_use = 0;
+	}
+
+	dev->cache_hits = 0;
+
+	if (!init_failed) {
+		dev->gc_cleanup_list =
+		    kmalloc(dev->param.chunks_per_block * sizeof(u32),
+					GFP_NOFS);
+		if (!dev->gc_cleanup_list)
+			init_failed = 1;
+	}
+
+	if (dev->param.is_yaffs2)
+		dev->param.use_header_file_size = 1;
+
+	if (!init_failed && !yaffs_init_blocks(dev))
+		init_failed = 1;
+
+	yaffs_init_tnodes_and_objs(dev);
+
+	if (!init_failed && !yaffs_create_initial_dir(dev))
+		init_failed = 1;
+
+	if (!init_failed) {
+		/* Now scan the flash. */
+		if (dev->param.is_yaffs2) {
+			if (yaffs2_checkpt_restore(dev)) {
+				yaffs_check_obj_details_loaded(dev->root_dir);
+				yaffs_trace(YAFFS_TRACE_CHECKPOINT | YAFFS_TRACE_MOUNT,
+					"yaffs: restored from checkpoint"
+					);
+			} else {
+
+				/* Clean up the mess caused by an aborted checkpoint load
+				 * and scan backwards.
+				 */
+				yaffs_deinit_blocks(dev);
+
+				yaffs_deinit_tnodes_and_objs(dev);
+
+				dev->n_erased_blocks = 0;
+				dev->n_free_chunks = 0;
+				dev->alloc_block = -1;
+				dev->alloc_page = -1;
+				dev->n_deleted_files = 0;
+				dev->n_unlinked_files = 0;
+				dev->n_bg_deletions = 0;
+
+				if (!init_failed && !yaffs_init_blocks(dev))
+					init_failed = 1;
+
+				yaffs_init_tnodes_and_objs(dev);
+
+				if (!init_failed
+				    && !yaffs_create_initial_dir(dev))
+					init_failed = 1;
+
+				if (!init_failed && !yaffs2_scan_backwards(dev))
+					init_failed = 1;
+			}
+		} else if (!yaffs1_scan(dev)) {
+			init_failed = 1;
+                }
+
+		yaffs_strip_deleted_objs(dev);
+		yaffs_fix_hanging_objs(dev);
+		if (dev->param.empty_lost_n_found)
+			yaffs_empty_l_n_f(dev);
+	}
+
+	if (init_failed) {
+		/* Clean up the mess */
+		yaffs_trace(YAFFS_TRACE_TRACING,
+		  "yaffs: yaffs_guts_initialise() aborted.");
+
+		yaffs_deinitialise(dev);
+		return YAFFS_FAIL;
+	}
+
+	/* Zero out stats */
+	dev->n_page_reads = 0;
+	dev->n_page_writes = 0;
+	dev->n_erasures = 0;
+	dev->n_gc_copies = 0;
+	dev->n_retired_writes = 0;
+
+	dev->n_retired_blocks = 0;
+
+	yaffs_verify_free_chunks(dev);
+	yaffs_verify_blocks(dev);
+
+	/* Clean up any aborted checkpoint data */
+	if (!dev->is_checkpointed && dev->blocks_in_checkpt > 0)
+		yaffs2_checkpt_invalidate(dev);
+
+	yaffs_trace(YAFFS_TRACE_TRACING,
+	  "yaffs: yaffs_guts_initialise() done.");
+	return YAFFS_OK;
+
+}
+
+void yaffs_deinitialise(struct yaffs_dev *dev)
+{
+	if (dev->is_mounted) {
+		int i;
+
+		yaffs_deinit_blocks(dev);
+		yaffs_deinit_tnodes_and_objs(dev);
+		if (dev->param.n_caches > 0 && dev->cache) {
+
+			for (i = 0; i < dev->param.n_caches; i++) {
+				if (dev->cache[i].data)
+					kfree(dev->cache[i].data);
+				dev->cache[i].data = NULL;
+			}
+
+			kfree(dev->cache);
+			dev->cache = NULL;
+		}
+
+		kfree(dev->gc_cleanup_list);
+
+		for (i = 0; i < YAFFS_N_TEMP_BUFFERS; i++)
+			kfree(dev->temp_buffer[i].buffer);
+
+		dev->is_mounted = 0;
+
+		if (dev->param.deinitialise_flash_fn)
+			dev->param.deinitialise_flash_fn(dev);
+	}
+}
+
+int yaffs_count_free_chunks(struct yaffs_dev *dev)
+{
+	int n_free = 0;
+	int b;
+
+	struct yaffs_block_info *blk;
+
+	blk = dev->block_info;
+	for (b = dev->internal_start_block; b <= dev->internal_end_block; b++) {
+		switch (blk->block_state) {
+		case YAFFS_BLOCK_STATE_EMPTY:
+		case YAFFS_BLOCK_STATE_ALLOCATING:
+		case YAFFS_BLOCK_STATE_COLLECTING:
+		case YAFFS_BLOCK_STATE_FULL:
+			n_free +=
+			    (dev->param.chunks_per_block - blk->pages_in_use +
+			     blk->soft_del_pages);
+			break;
+		default:
+			break;
+		}
+		blk++;
+	}
+
+	return n_free;
+}
+
+int yaffs_get_n_free_chunks(struct yaffs_dev *dev)
+{
+	/* This is what we report to the outside world */
+
+	int n_free;
+	int n_dirty_caches;
+	int blocks_for_checkpt;
+	int i;
+
+	n_free = dev->n_free_chunks;
+	n_free += dev->n_deleted_files;
+
+	/* Now count the number of dirty chunks in the cache and subtract those */
+
+	for (n_dirty_caches = 0, i = 0; i < dev->param.n_caches; i++) {
+		if (dev->cache[i].dirty)
+			n_dirty_caches++;
+	}
+
+	n_free -= n_dirty_caches;
+
+	n_free -=
+	    ((dev->param.n_reserved_blocks + 1) * dev->param.chunks_per_block);
+
+	/* Now we figure out how much to reserve for the checkpoint and report that... */
+	blocks_for_checkpt = yaffs_calc_checkpt_blocks_required(dev);
+
+	n_free -= (blocks_for_checkpt * dev->param.chunks_per_block);
+
+	if (n_free < 0)
+		n_free = 0;
+
+	return n_free;
+
+}
diff --git a/fs/yaffs2/yaffs_guts.h b/fs/yaffs2/yaffs_guts.h
new file mode 100644
index 0000000..307eba2
--- /dev/null
+++ b/fs/yaffs2/yaffs_guts.h
@@ -0,0 +1,915 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+#ifndef __YAFFS_GUTS_H__
+#define __YAFFS_GUTS_H__
+
+#include "yportenv.h"
+
+#define YAFFS_OK	1
+#define YAFFS_FAIL  0
+
+/* Give us a  Y=0x59,
+ * Give us an A=0x41,
+ * Give us an FF=0xFF
+ * Give us an S=0x53
+ * And what have we got...
+ */
+#define YAFFS_MAGIC			0x5941FF53
+
+#define YAFFS_NTNODES_LEVEL0	  	16
+#define YAFFS_TNODES_LEVEL0_BITS	4
+#define YAFFS_TNODES_LEVEL0_MASK	0xf
+
+#define YAFFS_NTNODES_INTERNAL 		(YAFFS_NTNODES_LEVEL0 / 2)
+#define YAFFS_TNODES_INTERNAL_BITS 	(YAFFS_TNODES_LEVEL0_BITS - 1)
+#define YAFFS_TNODES_INTERNAL_MASK	0x7
+#define YAFFS_TNODES_MAX_LEVEL		6
+
+#ifndef CONFIG_YAFFS_NO_YAFFS1
+#define YAFFS_BYTES_PER_SPARE		16
+#define YAFFS_BYTES_PER_CHUNK		512
+#define YAFFS_CHUNK_SIZE_SHIFT		9
+#define YAFFS_CHUNKS_PER_BLOCK		32
+#define YAFFS_BYTES_PER_BLOCK		(YAFFS_CHUNKS_PER_BLOCK*YAFFS_BYTES_PER_CHUNK)
+#endif
+
+#define YAFFS_MIN_YAFFS2_CHUNK_SIZE 	1024
+#define YAFFS_MIN_YAFFS2_SPARE_SIZE	32
+
+#define YAFFS_MAX_CHUNK_ID		0x000FFFFF
+
+#define YAFFS_ALLOCATION_NOBJECTS	100
+#define YAFFS_ALLOCATION_NTNODES	100
+#define YAFFS_ALLOCATION_NLINKS		100
+
+#define YAFFS_NOBJECT_BUCKETS		256
+
+#define YAFFS_OBJECT_SPACE		0x40000
+#define YAFFS_MAX_OBJECT_ID		(YAFFS_OBJECT_SPACE -1)
+
+#define YAFFS_CHECKPOINT_VERSION 	4
+
+#ifdef CONFIG_YAFFS_UNICODE
+#define YAFFS_MAX_NAME_LENGTH		127
+#define YAFFS_MAX_ALIAS_LENGTH		79
+#else
+#define YAFFS_MAX_NAME_LENGTH		255
+#define YAFFS_MAX_ALIAS_LENGTH		159
+#endif
+
+#define YAFFS_SHORT_NAME_LENGTH		15
+
+/* Some special object ids for pseudo objects */
+#define YAFFS_OBJECTID_ROOT		1
+#define YAFFS_OBJECTID_LOSTNFOUND	2
+#define YAFFS_OBJECTID_UNLINKED		3
+#define YAFFS_OBJECTID_DELETED		4
+
+/* Pseudo object ids for checkpointing */
+#define YAFFS_OBJECTID_SB_HEADER	0x10
+#define YAFFS_OBJECTID_CHECKPOINT_DATA	0x20
+#define YAFFS_SEQUENCE_CHECKPOINT_DATA  0x21
+
+#define YAFFS_MAX_SHORT_OP_CACHES	20
+
+#define YAFFS_N_TEMP_BUFFERS		6
+
+/* We limit the number attempts at sucessfully saving a chunk of data.
+ * Small-page devices have 32 pages per block; large-page devices have 64.
+ * Default to something in the order of 5 to 10 blocks worth of chunks.
+ */
+#define YAFFS_WR_ATTEMPTS		(5*64)
+
+/* Sequence numbers are used in YAFFS2 to determine block allocation order.
+ * The range is limited slightly to help distinguish bad numbers from good.
+ * This also allows us to perhaps in the future use special numbers for
+ * special purposes.
+ * EFFFFF00 allows the allocation of 8 blocks per second (~1Mbytes) for 15 years,
+ * and is a larger number than the lifetime of a 2GB device.
+ */
+#define YAFFS_LOWEST_SEQUENCE_NUMBER	0x00001000
+#define YAFFS_HIGHEST_SEQUENCE_NUMBER	0xEFFFFF00
+
+/* Special sequence number for bad block that failed to be marked bad */
+#define YAFFS_SEQUENCE_BAD_BLOCK	0xFFFF0000
+
+/* ChunkCache is used for short read/write operations.*/
+struct yaffs_cache {
+	struct yaffs_obj *object;
+	int chunk_id;
+	int last_use;
+	int dirty;
+	int n_bytes;		/* Only valid if the cache is dirty */
+	int locked;		/* Can't push out or flush while locked. */
+	u8 *data;
+};
+
+/* Tags structures in RAM
+ * NB This uses bitfield. Bitfields should not straddle a u32 boundary otherwise
+ * the structure size will get blown out.
+ */
+
+#ifndef CONFIG_YAFFS_NO_YAFFS1
+struct yaffs_tags {
+	unsigned chunk_id:20;
+	unsigned serial_number:2;
+	unsigned n_bytes_lsb:10;
+	unsigned obj_id:18;
+	unsigned ecc:12;
+	unsigned n_bytes_msb:2;
+};
+
+union yaffs_tags_union {
+	struct yaffs_tags as_tags;
+	u8 as_bytes[8];
+};
+
+#endif
+
+/* Stuff used for extended tags in YAFFS2 */
+
+enum yaffs_ecc_result {
+	YAFFS_ECC_RESULT_UNKNOWN,
+	YAFFS_ECC_RESULT_NO_ERROR,
+	YAFFS_ECC_RESULT_FIXED,
+	YAFFS_ECC_RESULT_UNFIXED
+};
+
+enum yaffs_obj_type {
+	YAFFS_OBJECT_TYPE_UNKNOWN,
+	YAFFS_OBJECT_TYPE_FILE,
+	YAFFS_OBJECT_TYPE_SYMLINK,
+	YAFFS_OBJECT_TYPE_DIRECTORY,
+	YAFFS_OBJECT_TYPE_HARDLINK,
+	YAFFS_OBJECT_TYPE_SPECIAL
+};
+
+#define YAFFS_OBJECT_TYPE_MAX YAFFS_OBJECT_TYPE_SPECIAL
+
+struct yaffs_ext_tags {
+
+	unsigned validity0;
+	unsigned chunk_used;	/*  Status of the chunk: used or unused */
+	unsigned obj_id;	/* If 0 then this is not part of an object (unused) */
+	unsigned chunk_id;	/* If 0 then this is a header, else a data chunk */
+	unsigned n_bytes;	/* Only valid for data chunks */
+
+	/* The following stuff only has meaning when we read */
+	enum yaffs_ecc_result ecc_result;
+	unsigned block_bad;
+
+	/* YAFFS 1 stuff */
+	unsigned is_deleted;	/* The chunk is marked deleted */
+	unsigned serial_number;	/* Yaffs1 2-bit serial number */
+
+	/* YAFFS2 stuff */
+	unsigned seq_number;	/* The sequence number of this block */
+
+	/* Extra info if this is an object header (YAFFS2 only) */
+
+	unsigned extra_available;	/* There is extra info available if this is not zero */
+	unsigned extra_parent_id;	/* The parent object */
+	unsigned extra_is_shrink;	/* Is it a shrink header? */
+	unsigned extra_shadows;	/* Does this shadow another object? */
+
+	enum yaffs_obj_type extra_obj_type;	/* What object type? */
+
+	unsigned extra_length;	/* Length if it is a file */
+	unsigned extra_equiv_id;	/* Equivalent object Id if it is a hard link */
+
+	unsigned validity1;
+
+};
+
+/* Spare structure for YAFFS1 */
+struct yaffs_spare {
+	u8 tb0;
+	u8 tb1;
+	u8 tb2;
+	u8 tb3;
+	u8 page_status;		/* set to 0 to delete the chunk */
+	u8 block_status;
+	u8 tb4;
+	u8 tb5;
+	u8 ecc1[3];
+	u8 tb6;
+	u8 tb7;
+	u8 ecc2[3];
+};
+
+/*Special structure for passing through to mtd */
+struct yaffs_nand_spare {
+	struct yaffs_spare spare;
+	int eccres1;
+	int eccres2;
+};
+
+/* Block data in RAM */
+
+enum yaffs_block_state {
+	YAFFS_BLOCK_STATE_UNKNOWN = 0,
+
+	YAFFS_BLOCK_STATE_SCANNING,
+	/* Being scanned */
+
+	YAFFS_BLOCK_STATE_NEEDS_SCANNING,
+	/* The block might have something on it (ie it is allocating or full, perhaps empty)
+	 * but it needs to be scanned to determine its true state.
+	 * This state is only valid during scanning.
+	 * NB We tolerate empty because the pre-scanner might be incapable of deciding
+	 * However, if this state is returned on a YAFFS2 device, then we expect a sequence number
+	 */
+
+	YAFFS_BLOCK_STATE_EMPTY,
+	/* This block is empty */
+
+	YAFFS_BLOCK_STATE_ALLOCATING,
+	/* This block is partially allocated.
+	 * At least one page holds valid data.
+	 * This is the one currently being used for page
+	 * allocation. Should never be more than one of these.
+	 * If a block is only partially allocated at mount it is treated as full.
+	 */
+
+	YAFFS_BLOCK_STATE_FULL,
+	/* All the pages in this block have been allocated.
+	 * If a block was only partially allocated when mounted we treat
+	 * it as fully allocated.
+	 */
+
+	YAFFS_BLOCK_STATE_DIRTY,
+	/* The block was full and now all chunks have been deleted.
+	 * Erase me, reuse me.
+	 */
+
+	YAFFS_BLOCK_STATE_CHECKPOINT,
+	/* This block is assigned to holding checkpoint data. */
+
+	YAFFS_BLOCK_STATE_COLLECTING,
+	/* This block is being garbage collected */
+
+	YAFFS_BLOCK_STATE_DEAD
+	    /* This block has failed and is not in use */
+};
+
+#define	YAFFS_NUMBER_OF_BLOCK_STATES (YAFFS_BLOCK_STATE_DEAD + 1)
+
+struct yaffs_block_info {
+
+	int soft_del_pages:10;	/* number of soft deleted pages */
+	int pages_in_use:10;	/* number of pages in use */
+	unsigned block_state:4;	/* One of the above block states. NB use unsigned because enum is sometimes an int */
+	u32 needs_retiring:1;	/* Data has failed on this block, need to get valid data off */
+	/* and retire the block. */
+	u32 skip_erased_check:1;	/* If this is set we can skip the erased check on this block */
+	u32 gc_prioritise:1;	/* An ECC check or blank check has failed on this block.
+				   It should be prioritised for GC */
+	u32 chunk_error_strikes:3;	/* How many times we've had ecc etc failures on this block and tried to reuse it */
+
+#ifdef CONFIG_YAFFS_YAFFS2
+	u32 has_shrink_hdr:1;	/* This block has at least one shrink object header */
+	u32 seq_number;		/* block sequence number for yaffs2 */
+#endif
+
+};
+
+/* -------------------------- Object structure -------------------------------*/
+/* This is the object structure as stored on NAND */
+
+struct yaffs_obj_hdr {
+	enum yaffs_obj_type type;
+
+	/* Apply to everything  */
+	int parent_obj_id;
+	u16 sum_no_longer_used;	/* checksum of name. No longer used */
+	YCHAR name[YAFFS_MAX_NAME_LENGTH + 1];
+
+	/* The following apply to directories, files, symlinks - not hard links */
+	u32 yst_mode;		/* protection */
+
+	u32 yst_uid;
+	u32 yst_gid;
+	u32 yst_atime;
+	u32 yst_mtime;
+	u32 yst_ctime;
+
+	/* File size  applies to files only */
+	int file_size;
+
+	/* Equivalent object id applies to hard links only. */
+	int equiv_id;
+
+	/* Alias is for symlinks only. */
+	YCHAR alias[YAFFS_MAX_ALIAS_LENGTH + 1];
+
+	u32 yst_rdev;		/* device stuff for block and char devices (major/min) */
+
+	u32 win_ctime[2];
+	u32 win_atime[2];
+	u32 win_mtime[2];
+
+	u32 inband_shadowed_obj_id;
+	u32 inband_is_shrink;
+
+	u32 reserved[2];
+	int shadows_obj;	/* This object header shadows the specified object if > 0 */
+
+	/* is_shrink applies to object headers written when we shrink the file (ie resize) */
+	u32 is_shrink;
+
+};
+
+/*--------------------------- Tnode -------------------------- */
+
+struct yaffs_tnode {
+	struct yaffs_tnode *internal[YAFFS_NTNODES_INTERNAL];
+};
+
+/*------------------------  Object -----------------------------*/
+/* An object can be one of:
+ * - a directory (no data, has children links
+ * - a regular file (data.... not prunes :->).
+ * - a symlink [symbolic link] (the alias).
+ * - a hard link
+ */
+
+struct yaffs_file_var {
+	u32 file_size;
+	u32 scanned_size;
+	u32 shrink_size;
+	int top_level;
+	struct yaffs_tnode *top;
+};
+
+struct yaffs_dir_var {
+	struct list_head children;	/* list of child links */
+	struct list_head dirty;	/* Entry for list of dirty directories */
+};
+
+struct yaffs_symlink_var {
+	YCHAR *alias;
+};
+
+struct yaffs_hardlink_var {
+	struct yaffs_obj *equiv_obj;
+	u32 equiv_id;
+};
+
+union yaffs_obj_var {
+	struct yaffs_file_var file_variant;
+	struct yaffs_dir_var dir_variant;
+	struct yaffs_symlink_var symlink_variant;
+	struct yaffs_hardlink_var hardlink_variant;
+};
+
+struct yaffs_obj {
+	u8 deleted:1;		/* This should only apply to unlinked files. */
+	u8 soft_del:1;		/* it has also been soft deleted */
+	u8 unlinked:1;		/* An unlinked file. The file should be in the unlinked directory. */
+	u8 fake:1;		/* A fake object has no presence on NAND. */
+	u8 rename_allowed:1;	/* Some objects are not allowed to be renamed. */
+	u8 unlink_allowed:1;
+	u8 dirty:1;		/* the object needs to be written to flash */
+	u8 valid:1;		/* When the file system is being loaded up, this
+				 * object might be created before the data
+				 * is available (ie. file data records appear before the header).
+				 */
+	u8 lazy_loaded:1;	/* This object has been lazy loaded and is missing some detail */
+
+	u8 defered_free:1;	/* For Linux kernel. Object is removed from NAND, but is
+				 * still in the inode cache. Free of object is defered.
+				 * until the inode is released.
+				 */
+	u8 being_created:1;	/* This object is still being created so skip some checks. */
+	u8 is_shadowed:1;	/* This object is shadowed on the way to being renamed. */
+
+	u8 xattr_known:1;	/* We know if this has object has xattribs or not. */
+	u8 has_xattr:1;		/* This object has xattribs. Valid if xattr_known. */
+
+	u8 serial;		/* serial number of chunk in NAND. Cached here */
+	u16 sum;		/* sum of the name to speed searching */
+
+	struct yaffs_dev *my_dev;	/* The device I'm on */
+
+	struct list_head hash_link;	/* list of objects in this hash bucket */
+
+	struct list_head hard_links;	/* all the equivalent hard linked objects */
+
+	/* directory structure stuff */
+	/* also used for linking up the free list */
+	struct yaffs_obj *parent;
+	struct list_head siblings;
+
+	/* Where's my object header in NAND? */
+	int hdr_chunk;
+
+	int n_data_chunks;	/* Number of data chunks attached to the file. */
+
+	u32 obj_id;		/* the object id value */
+
+	u32 yst_mode;
+
+#ifndef CONFIG_YAFFS_NO_SHORT_NAMES
+	YCHAR short_name[YAFFS_SHORT_NAME_LENGTH + 1];
+#endif
+
+#ifdef CONFIG_YAFFS_WINCE
+	u32 win_ctime[2];
+	u32 win_mtime[2];
+	u32 win_atime[2];
+#else
+	u32 yst_uid;
+	u32 yst_gid;
+	u32 yst_atime;
+	u32 yst_mtime;
+	u32 yst_ctime;
+#endif
+
+	u32 yst_rdev;
+
+	void *my_inode;
+
+	enum yaffs_obj_type variant_type;
+
+	union yaffs_obj_var variant;
+
+};
+
+struct yaffs_obj_bucket {
+	struct list_head list;
+	int count;
+};
+
+/* yaffs_checkpt_obj holds the definition of an object as dumped
+ * by checkpointing.
+ */
+
+struct yaffs_checkpt_obj {
+	int struct_type;
+	u32 obj_id;
+	u32 parent_id;
+	int hdr_chunk;
+	enum yaffs_obj_type variant_type:3;
+	u8 deleted:1;
+	u8 soft_del:1;
+	u8 unlinked:1;
+	u8 fake:1;
+	u8 rename_allowed:1;
+	u8 unlink_allowed:1;
+	u8 serial;
+	int n_data_chunks;
+	u32 size_or_equiv_obj;
+};
+
+/*--------------------- Temporary buffers ----------------
+ *
+ * These are chunk-sized working buffers. Each device has a few
+ */
+
+struct yaffs_buffer {
+	u8 *buffer;
+	int line;		/* track from whence this buffer was allocated */
+	int max_line;
+};
+
+/*----------------- Device ---------------------------------*/
+
+struct yaffs_param {
+	const YCHAR *name;
+
+	/*
+	 * Entry parameters set up way early. Yaffs sets up the rest.
+	 * The structure should be zeroed out before use so that unused
+	 * and defualt values are zero.
+	 */
+
+	int inband_tags;	/* Use unband tags */
+	u32 total_bytes_per_chunk;	/* Should be >= 512, does not need to be a power of 2 */
+	int chunks_per_block;	/* does not need to be a power of 2 */
+	int spare_bytes_per_chunk;	/* spare area size */
+	int start_block;	/* Start block we're allowed to use */
+	int end_block;		/* End block we're allowed to use */
+	int n_reserved_blocks;	/* We want this tuneable so that we can reduce */
+	/* reserved blocks on NOR and RAM. */
+
+	int n_caches;		/* If <= 0, then short op caching is disabled, else
+				 * the number of short op caches (don't use too many).
+				 * 10 to 20 is a good bet.
+				 */
+	int use_nand_ecc;	/* Flag to decide whether or not to use NANDECC on data (yaffs1) */
+	int no_tags_ecc;	/* Flag to decide whether or not to do ECC on packed tags (yaffs2) */
+
+	int is_yaffs2;		/* Use yaffs2 mode on this device */
+
+	int empty_lost_n_found;	/* Auto-empty lost+found directory on mount */
+
+	int refresh_period;	/* How often we should check to do a block refresh */
+
+	/* Checkpoint control. Can be set before or after initialisation */
+	u8 skip_checkpt_rd;
+	u8 skip_checkpt_wr;
+
+	int enable_xattr;	/* Enable xattribs */
+
+	/* NAND access functions (Must be set before calling YAFFS) */
+
+	int (*write_chunk_fn) (struct yaffs_dev * dev,
+			       int nand_chunk, const u8 * data,
+			       const struct yaffs_spare * spare);
+	int (*read_chunk_fn) (struct yaffs_dev * dev,
+			      int nand_chunk, u8 * data,
+			      struct yaffs_spare * spare);
+	int (*erase_fn) (struct yaffs_dev * dev, int flash_block);
+	int (*initialise_flash_fn) (struct yaffs_dev * dev);
+	int (*deinitialise_flash_fn) (struct yaffs_dev * dev);
+
+#ifdef CONFIG_YAFFS_YAFFS2
+	int (*write_chunk_tags_fn) (struct yaffs_dev * dev,
+				    int nand_chunk, const u8 * data,
+				    const struct yaffs_ext_tags * tags);
+	int (*read_chunk_tags_fn) (struct yaffs_dev * dev,
+				   int nand_chunk, u8 * data,
+				   struct yaffs_ext_tags * tags);
+	int (*bad_block_fn) (struct yaffs_dev * dev, int block_no);
+	int (*query_block_fn) (struct yaffs_dev * dev, int block_no,
+			       enum yaffs_block_state * state,
+			       u32 * seq_number);
+#endif
+
+	/* The remove_obj_fn function must be supplied by OS flavours that
+	 * need it.
+	 * yaffs direct uses it to implement the faster readdir.
+	 * Linux uses it to protect the directory during unlocking.
+	 */
+	void (*remove_obj_fn) (struct yaffs_obj * obj);
+
+	/* Callback to mark the superblock dirty */
+	void (*sb_dirty_fn) (struct yaffs_dev * dev);
+
+	/*  Callback to control garbage collection. */
+	unsigned (*gc_control) (struct yaffs_dev * dev);
+
+	/* Debug control flags. Don't use unless you know what you're doing */
+	int use_header_file_size;	/* Flag to determine if we should use file sizes from the header */
+	int disable_lazy_load;	/* Disable lazy loading on this device */
+	int wide_tnodes_disabled;	/* Set to disable wide tnodes */
+	int disable_soft_del;	/* yaffs 1 only: Set to disable the use of softdeletion. */
+
+	int defered_dir_update;	/* Set to defer directory updates */
+
+#ifdef CONFIG_YAFFS_AUTO_UNICODE
+	int auto_unicode;
+#endif
+	int always_check_erased;	/* Force chunk erased check always on */
+};
+
+struct yaffs_dev {
+	struct yaffs_param param;
+
+	/* Context storage. Holds extra OS specific data for this device */
+
+	void *os_context;
+	void *driver_context;
+
+	struct list_head dev_list;
+
+	/* Runtime parameters. Set up by YAFFS. */
+	int data_bytes_per_chunk;
+
+	/* Non-wide tnode stuff */
+	u16 chunk_grp_bits;	/* Number of bits that need to be resolved if
+				 * the tnodes are not wide enough.
+				 */
+	u16 chunk_grp_size;	/* == 2^^chunk_grp_bits */
+
+	/* Stuff to support wide tnodes */
+	u32 tnode_width;
+	u32 tnode_mask;
+	u32 tnode_size;
+
+	/* Stuff for figuring out file offset to chunk conversions */
+	u32 chunk_shift;	/* Shift value */
+	u32 chunk_div;		/* Divisor after shifting: 1 for power-of-2 sizes */
+	u32 chunk_mask;		/* Mask to use for power-of-2 case */
+
+	int is_mounted;
+	int read_only;
+	int is_checkpointed;
+
+	/* Stuff to support block offsetting to support start block zero */
+	int internal_start_block;
+	int internal_end_block;
+	int block_offset;
+	int chunk_offset;
+
+	/* Runtime checkpointing stuff */
+	int checkpt_page_seq;	/* running sequence number of checkpoint pages */
+	int checkpt_byte_count;
+	int checkpt_byte_offs;
+	u8 *checkpt_buffer;
+	int checkpt_open_write;
+	int blocks_in_checkpt;
+	int checkpt_cur_chunk;
+	int checkpt_cur_block;
+	int checkpt_next_block;
+	int *checkpt_block_list;
+	int checkpt_max_blocks;
+	u32 checkpt_sum;
+	u32 checkpt_xor;
+
+	int checkpoint_blocks_required;	/* Number of blocks needed to store current checkpoint set */
+
+	/* Block Info */
+	struct yaffs_block_info *block_info;
+	u8 *chunk_bits;		/* bitmap of chunks in use */
+	unsigned block_info_alt:1;	/* was allocated using alternative strategy */
+	unsigned chunk_bits_alt:1;	/* was allocated using alternative strategy */
+	int chunk_bit_stride;	/* Number of bytes of chunk_bits per block.
+				 * Must be consistent with chunks_per_block.
+				 */
+
+	int n_erased_blocks;
+	int alloc_block;	/* Current block being allocated off */
+	u32 alloc_page;
+	int alloc_block_finder;	/* Used to search for next allocation block */
+
+	/* Object and Tnode memory management */
+	void *allocator;
+	int n_obj;
+	int n_tnodes;
+
+	int n_hardlinks;
+
+	struct yaffs_obj_bucket obj_bucket[YAFFS_NOBJECT_BUCKETS];
+	u32 bucket_finder;
+
+	int n_free_chunks;
+
+	/* Garbage collection control */
+	u32 *gc_cleanup_list;	/* objects to delete at the end of a GC. */
+	u32 n_clean_ups;
+
+	unsigned has_pending_prioritised_gc;	/* We think this device might have pending prioritised gcs */
+	unsigned gc_disable;
+	unsigned gc_block_finder;
+	unsigned gc_dirtiest;
+	unsigned gc_pages_in_use;
+	unsigned gc_not_done;
+	unsigned gc_block;
+	unsigned gc_chunk;
+	unsigned gc_skip;
+
+	/* Special directories */
+	struct yaffs_obj *root_dir;
+	struct yaffs_obj *lost_n_found;
+
+	/* Buffer areas for storing data to recover from write failures TODO
+	 *      u8            buffered_data[YAFFS_CHUNKS_PER_BLOCK][YAFFS_BYTES_PER_CHUNK];
+	 *      struct yaffs_spare buffered_spare[YAFFS_CHUNKS_PER_BLOCK];
+	 */
+
+	int buffered_block;	/* Which block is buffered here? */
+	int doing_buffered_block_rewrite;
+
+	struct yaffs_cache *cache;
+	int cache_last_use;
+
+	/* Stuff for background deletion and unlinked files. */
+	struct yaffs_obj *unlinked_dir;	/* Directory where unlinked and deleted files live. */
+	struct yaffs_obj *del_dir;	/* Directory where deleted objects are sent to disappear. */
+	struct yaffs_obj *unlinked_deletion;	/* Current file being background deleted. */
+	int n_deleted_files;	/* Count of files awaiting deletion; */
+	int n_unlinked_files;	/* Count of unlinked files. */
+	int n_bg_deletions;	/* Count of background deletions. */
+
+	/* Temporary buffer management */
+	struct yaffs_buffer temp_buffer[YAFFS_N_TEMP_BUFFERS];
+	int max_temp;
+	int temp_in_use;
+	int unmanaged_buffer_allocs;
+	int unmanaged_buffer_deallocs;
+
+	/* yaffs2 runtime stuff */
+	unsigned seq_number;	/* Sequence number of currently allocating block */
+	unsigned oldest_dirty_seq;
+	unsigned oldest_dirty_block;
+
+	/* Block refreshing */
+	int refresh_skip;	/* A skip down counter. Refresh happens when this gets to zero. */
+
+	/* Dirty directory handling */
+	struct list_head dirty_dirs;	/* List of dirty directories */
+
+	/* Statistcs */
+	u32 n_page_writes;
+	u32 n_page_reads;
+	u32 n_erasures;
+	u32 n_erase_failures;
+	u32 n_gc_copies;
+	u32 all_gcs;
+	u32 passive_gc_count;
+	u32 oldest_dirty_gc_count;
+	u32 n_gc_blocks;
+	u32 bg_gcs;
+	u32 n_retired_writes;
+	u32 n_retired_blocks;
+	u32 n_ecc_fixed;
+	u32 n_ecc_unfixed;
+	u32 n_tags_ecc_fixed;
+	u32 n_tags_ecc_unfixed;
+	u32 n_deletions;
+	u32 n_unmarked_deletions;
+	u32 refresh_count;
+	u32 cache_hits;
+
+};
+
+/* The CheckpointDevice structure holds the device information that changes at runtime and
+ * must be preserved over unmount/mount cycles.
+ */
+struct yaffs_checkpt_dev {
+	int struct_type;
+	int n_erased_blocks;
+	int alloc_block;	/* Current block being allocated off */
+	u32 alloc_page;
+	int n_free_chunks;
+
+	int n_deleted_files;	/* Count of files awaiting deletion; */
+	int n_unlinked_files;	/* Count of unlinked files. */
+	int n_bg_deletions;	/* Count of background deletions. */
+
+	/* yaffs2 runtime stuff */
+	unsigned seq_number;	/* Sequence number of currently allocating block */
+
+};
+
+struct yaffs_checkpt_validity {
+	int struct_type;
+	u32 magic;
+	u32 version;
+	u32 head;
+};
+
+struct yaffs_shadow_fixer {
+	int obj_id;
+	int shadowed_id;
+	struct yaffs_shadow_fixer *next;
+};
+
+/* Structure for doing xattr modifications */
+struct yaffs_xattr_mod {
+	int set;		/* If 0 then this is a deletion */
+	const YCHAR *name;
+	const void *data;
+	int size;
+	int flags;
+	int result;
+};
+
+/*----------------------- YAFFS Functions -----------------------*/
+
+int yaffs_guts_initialise(struct yaffs_dev *dev);
+void yaffs_deinitialise(struct yaffs_dev *dev);
+
+int yaffs_get_n_free_chunks(struct yaffs_dev *dev);
+
+int yaffs_rename_obj(struct yaffs_obj *old_dir, const YCHAR * old_name,
+		     struct yaffs_obj *new_dir, const YCHAR * new_name);
+
+int yaffs_unlinker(struct yaffs_obj *dir, const YCHAR * name);
+int yaffs_del_obj(struct yaffs_obj *obj);
+
+int yaffs_get_obj_name(struct yaffs_obj *obj, YCHAR * name, int buffer_size);
+int yaffs_get_obj_length(struct yaffs_obj *obj);
+int yaffs_get_obj_inode(struct yaffs_obj *obj);
+unsigned yaffs_get_obj_type(struct yaffs_obj *obj);
+int yaffs_get_obj_link_count(struct yaffs_obj *obj);
+
+/* File operations */
+int yaffs_file_rd(struct yaffs_obj *obj, u8 * buffer, loff_t offset,
+		  int n_bytes);
+int yaffs_wr_file(struct yaffs_obj *obj, const u8 * buffer, loff_t offset,
+		  int n_bytes, int write_trhrough);
+int yaffs_resize_file(struct yaffs_obj *obj, loff_t new_size);
+
+struct yaffs_obj *yaffs_create_file(struct yaffs_obj *parent,
+				    const YCHAR * name, u32 mode, u32 uid,
+				    u32 gid);
+
+int yaffs_flush_file(struct yaffs_obj *obj, int update_time, int data_sync);
+
+/* Flushing and checkpointing */
+void yaffs_flush_whole_cache(struct yaffs_dev *dev);
+
+int yaffs_checkpoint_save(struct yaffs_dev *dev);
+int yaffs_checkpoint_restore(struct yaffs_dev *dev);
+
+/* Directory operations */
+struct yaffs_obj *yaffs_create_dir(struct yaffs_obj *parent, const YCHAR * name,
+				   u32 mode, u32 uid, u32 gid);
+struct yaffs_obj *yaffs_find_by_name(struct yaffs_obj *the_dir,
+				     const YCHAR * name);
+struct yaffs_obj *yaffs_find_by_number(struct yaffs_dev *dev, u32 number);
+
+/* Link operations */
+struct yaffs_obj *yaffs_link_obj(struct yaffs_obj *parent, const YCHAR * name,
+				 struct yaffs_obj *equiv_obj);
+
+struct yaffs_obj *yaffs_get_equivalent_obj(struct yaffs_obj *obj);
+
+/* Symlink operations */
+struct yaffs_obj *yaffs_create_symlink(struct yaffs_obj *parent,
+				       const YCHAR * name, u32 mode, u32 uid,
+				       u32 gid, const YCHAR * alias);
+YCHAR *yaffs_get_symlink_alias(struct yaffs_obj *obj);
+
+/* Special inodes (fifos, sockets and devices) */
+struct yaffs_obj *yaffs_create_special(struct yaffs_obj *parent,
+				       const YCHAR * name, u32 mode, u32 uid,
+				       u32 gid, u32 rdev);
+
+int yaffs_set_xattrib(struct yaffs_obj *obj, const YCHAR * name,
+		      const void *value, int size, int flags);
+int yaffs_get_xattrib(struct yaffs_obj *obj, const YCHAR * name, void *value,
+		      int size);
+int yaffs_list_xattrib(struct yaffs_obj *obj, char *buffer, int size);
+int yaffs_remove_xattrib(struct yaffs_obj *obj, const YCHAR * name);
+
+/* Special directories */
+struct yaffs_obj *yaffs_root(struct yaffs_dev *dev);
+struct yaffs_obj *yaffs_lost_n_found(struct yaffs_dev *dev);
+
+void yaffs_handle_defered_free(struct yaffs_obj *obj);
+
+void yaffs_update_dirty_dirs(struct yaffs_dev *dev);
+
+int yaffs_bg_gc(struct yaffs_dev *dev, unsigned urgency);
+
+/* Debug dump  */
+int yaffs_dump_obj(struct yaffs_obj *obj);
+
+void yaffs_guts_test(struct yaffs_dev *dev);
+
+/* A few useful functions to be used within the core files*/
+void yaffs_chunk_del(struct yaffs_dev *dev, int chunk_id, int mark_flash,
+		     int lyn);
+int yaffs_check_ff(u8 * buffer, int n_bytes);
+void yaffs_handle_chunk_error(struct yaffs_dev *dev,
+			      struct yaffs_block_info *bi);
+
+u8 *yaffs_get_temp_buffer(struct yaffs_dev *dev, int line_no);
+void yaffs_release_temp_buffer(struct yaffs_dev *dev, u8 * buffer, int line_no);
+
+struct yaffs_obj *yaffs_find_or_create_by_number(struct yaffs_dev *dev,
+						 int number,
+						 enum yaffs_obj_type type);
+int yaffs_put_chunk_in_file(struct yaffs_obj *in, int inode_chunk,
+			    int nand_chunk, int in_scan);
+void yaffs_set_obj_name(struct yaffs_obj *obj, const YCHAR * name);
+void yaffs_set_obj_name_from_oh(struct yaffs_obj *obj,
+				const struct yaffs_obj_hdr *oh);
+void yaffs_add_obj_to_dir(struct yaffs_obj *directory, struct yaffs_obj *obj);
+YCHAR *yaffs_clone_str(const YCHAR * str);
+void yaffs_link_fixup(struct yaffs_dev *dev, struct yaffs_obj *hard_list);
+void yaffs_block_became_dirty(struct yaffs_dev *dev, int block_no);
+int yaffs_update_oh(struct yaffs_obj *in, const YCHAR * name,
+		    int force, int is_shrink, int shadows,
+		    struct yaffs_xattr_mod *xop);
+void yaffs_handle_shadowed_obj(struct yaffs_dev *dev, int obj_id,
+			       int backward_scanning);
+int yaffs_check_alloc_available(struct yaffs_dev *dev, int n_chunks);
+struct yaffs_tnode *yaffs_get_tnode(struct yaffs_dev *dev);
+struct yaffs_tnode *yaffs_add_find_tnode_0(struct yaffs_dev *dev,
+					   struct yaffs_file_var *file_struct,
+					   u32 chunk_id,
+					   struct yaffs_tnode *passed_tn);
+
+int yaffs_do_file_wr(struct yaffs_obj *in, const u8 * buffer, loff_t offset,
+		     int n_bytes, int write_trhrough);
+void yaffs_resize_file_down(struct yaffs_obj *obj, loff_t new_size);
+void yaffs_skip_rest_of_block(struct yaffs_dev *dev);
+
+int yaffs_count_free_chunks(struct yaffs_dev *dev);
+
+struct yaffs_tnode *yaffs_find_tnode_0(struct yaffs_dev *dev,
+				       struct yaffs_file_var *file_struct,
+				       u32 chunk_id);
+
+u32 yaffs_get_group_base(struct yaffs_dev *dev, struct yaffs_tnode *tn,
+			 unsigned pos);
+
+int yaffs_is_non_empty_dir(struct yaffs_obj *obj);
+#endif
diff --git a/fs/yaffs2/yaffs_linux.h b/fs/yaffs2/yaffs_linux.h
new file mode 100644
index 0000000..3b508cb
--- /dev/null
+++ b/fs/yaffs2/yaffs_linux.h
@@ -0,0 +1,41 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+#ifndef __YAFFS_LINUX_H__
+#define __YAFFS_LINUX_H__
+
+#include "yportenv.h"
+
+struct yaffs_linux_context {
+	struct list_head context_list;	/* List of these we have mounted */
+	struct yaffs_dev *dev;
+	struct super_block *super;
+	struct task_struct *bg_thread;	/* Background thread for this device */
+	int bg_running;
+	struct mutex gross_lock;	/* Gross locking mutex*/
+	u8 *spare_buffer;	/* For mtdif2 use. Don't know the size of the buffer
+				 * at compile time so we have to allocate it.
+				 */
+	struct list_head search_contexts;
+	void (*put_super_fn) (struct super_block * sb);
+
+	struct task_struct *readdir_process;
+	unsigned mount_id;
+};
+
+#define yaffs_dev_to_lc(dev) ((struct yaffs_linux_context *)((dev)->os_context))
+#define yaffs_dev_to_mtd(dev) ((struct mtd_info *)((dev)->driver_context))
+
+#endif
diff --git a/fs/yaffs2/yaffs_mtdif.c b/fs/yaffs2/yaffs_mtdif.c
new file mode 100644
index 0000000..7cf53b3
--- /dev/null
+++ b/fs/yaffs2/yaffs_mtdif.c
@@ -0,0 +1,54 @@
+/*
+ * YAFFS: Yet Another Flash File System. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU General Public License version 2 as
+ * published by the Free Software Foundation.
+ */
+
+#include "yportenv.h"
+
+#include "yaffs_mtdif.h"
+
+#include "linux/mtd/mtd.h"
+#include "linux/types.h"
+#include "linux/time.h"
+#include "linux/mtd/nand.h"
+
+#include "yaffs_linux.h"
+
+int nandmtd_erase_block(struct yaffs_dev *dev, int block_no)
+{
+	struct mtd_info *mtd = yaffs_dev_to_mtd(dev);
+	u32 addr =
+	    ((loff_t) block_no) * dev->param.total_bytes_per_chunk
+	    * dev->param.chunks_per_block;
+	struct erase_info ei;
+
+	int retval = 0;
+
+	ei.mtd = mtd;
+	ei.addr = addr;
+	ei.len = dev->param.total_bytes_per_chunk * dev->param.chunks_per_block;
+	ei.time = 1000;
+	ei.retries = 2;
+	ei.callback = NULL;
+	ei.priv = (u_long) dev;
+
+	retval = mtd->erase(mtd, &ei);
+
+	if (retval == 0)
+		return YAFFS_OK;
+	else
+		return YAFFS_FAIL;
+}
+
+int nandmtd_initialise(struct yaffs_dev *dev)
+{
+	return YAFFS_OK;
+}
diff --git a/fs/yaffs2/yaffs_mtdif.h b/fs/yaffs2/yaffs_mtdif.h
new file mode 100644
index 0000000..6665074
--- /dev/null
+++ b/fs/yaffs2/yaffs_mtdif.h
@@ -0,0 +1,23 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+#ifndef __YAFFS_MTDIF_H__
+#define __YAFFS_MTDIF_H__
+
+#include "yaffs_guts.h"
+
+int nandmtd_erase_block(struct yaffs_dev *dev, int block_no);
+int nandmtd_initialise(struct yaffs_dev *dev);
+#endif
diff --git a/fs/yaffs2/yaffs_mtdif1.c b/fs/yaffs2/yaffs_mtdif1.c
new file mode 100644
index 0000000..5108369
--- /dev/null
+++ b/fs/yaffs2/yaffs_mtdif1.c
@@ -0,0 +1,330 @@
+/*
+ * YAFFS: Yet another FFS. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU General Public License version 2 as
+ * published by the Free Software Foundation.
+ */
+
+/*
+ * This module provides the interface between yaffs_nand.c and the
+ * MTD API.  This version is used when the MTD interface supports the
+ * 'mtd_oob_ops' style calls to read_oob and write_oob, circa 2.6.17,
+ * and we have small-page NAND device.
+ *
+ * These functions are invoked via function pointers in yaffs_nand.c.
+ * This replaces functionality provided by functions in yaffs_mtdif.c
+ * and the yaffs_tags compatability functions in yaffs_tagscompat.c that are
+ * called in yaffs_mtdif.c when the function pointers are NULL.
+ * We assume the MTD layer is performing ECC (use_nand_ecc is true).
+ */
+
+#include "yportenv.h"
+#include "yaffs_trace.h"
+#include "yaffs_guts.h"
+#include "yaffs_packedtags1.h"
+#include "yaffs_tagscompat.h"	/* for yaffs_calc_tags_ecc */
+#include "yaffs_linux.h"
+
+#include "linux/kernel.h"
+#include "linux/version.h"
+#include "linux/types.h"
+#include "linux/mtd/mtd.h"
+
+#ifndef CONFIG_YAFFS_9BYTE_TAGS
+# define YTAG1_SIZE 8
+#else
+# define YTAG1_SIZE 9
+#endif
+
+/* Write a chunk (page) of data to NAND.
+ *
+ * Caller always provides ExtendedTags data which are converted to a more
+ * compact (packed) form for storage in NAND.  A mini-ECC runs over the
+ * contents of the tags meta-data; used to valid the tags when read.
+ *
+ *  - Pack ExtendedTags to packed_tags1 form
+ *  - Compute mini-ECC for packed_tags1
+ *  - Write data and packed tags to NAND.
+ *
+ * Note: Due to the use of the packed_tags1 meta-data which does not include
+ * a full sequence number (as found in the larger packed_tags2 form) it is
+ * necessary for Yaffs to re-write a chunk/page (just once) to mark it as
+ * discarded and dirty.  This is not ideal: newer NAND parts are supposed
+ * to be written just once.  When Yaffs performs this operation, this
+ * function is called with a NULL data pointer -- calling MTD write_oob
+ * without data is valid usage (2.6.17).
+ *
+ * Any underlying MTD error results in YAFFS_FAIL.
+ * Returns YAFFS_OK or YAFFS_FAIL.
+ */
+int nandmtd1_write_chunk_tags(struct yaffs_dev *dev,
+			      int nand_chunk, const u8 * data,
+			      const struct yaffs_ext_tags *etags)
+{
+	struct mtd_info *mtd = yaffs_dev_to_mtd(dev);
+	int chunk_bytes = dev->data_bytes_per_chunk;
+	loff_t addr = ((loff_t) nand_chunk) * chunk_bytes;
+	struct mtd_oob_ops ops;
+	struct yaffs_packed_tags1 pt1;
+	int retval;
+
+	/* we assume that packed_tags1 and struct yaffs_tags are compatible */
+	compile_time_assertion(sizeof(struct yaffs_packed_tags1) == 12);
+	compile_time_assertion(sizeof(struct yaffs_tags) == 8);
+
+	yaffs_pack_tags1(&pt1, etags);
+	yaffs_calc_tags_ecc((struct yaffs_tags *)&pt1);
+
+	/* When deleting a chunk, the upper layer provides only skeletal
+	 * etags, one with is_deleted set.  However, we need to update the
+	 * tags, not erase them completely.  So we use the NAND write property
+	 * that only zeroed-bits stick and set tag bytes to all-ones and
+	 * zero just the (not) deleted bit.
+	 */
+#ifndef CONFIG_YAFFS_9BYTE_TAGS
+	if (etags->is_deleted) {
+		memset(&pt1, 0xff, 8);
+		/* clear delete status bit to indicate deleted */
+		pt1.deleted = 0;
+	}
+#else
+	((u8 *) & pt1)[8] = 0xff;
+	if (etags->is_deleted) {
+		memset(&pt1, 0xff, 8);
+		/* zero page_status byte to indicate deleted */
+		((u8 *) & pt1)[8] = 0;
+	}
+#endif
+
+	memset(&ops, 0, sizeof(ops));
+	ops.mode = MTD_OOB_AUTO;
+	ops.len = (data) ? chunk_bytes : 0;
+	ops.ooblen = YTAG1_SIZE;
+	ops.datbuf = (u8 *) data;
+	ops.oobbuf = (u8 *) & pt1;
+
+	retval = mtd->write_oob(mtd, addr, &ops);
+	if (retval) {
+		yaffs_trace(YAFFS_TRACE_MTD,
+			"write_oob failed, chunk %d, mtd error %d",
+			nand_chunk, retval);
+	}
+	return retval ? YAFFS_FAIL : YAFFS_OK;
+}
+
+/* Return with empty ExtendedTags but add ecc_result.
+ */
+static int rettags(struct yaffs_ext_tags *etags, int ecc_result, int retval)
+{
+	if (etags) {
+		memset(etags, 0, sizeof(*etags));
+		etags->ecc_result = ecc_result;
+	}
+	return retval;
+}
+
+/* Read a chunk (page) from NAND.
+ *
+ * Caller expects ExtendedTags data to be usable even on error; that is,
+ * all members except ecc_result and block_bad are zeroed.
+ *
+ *  - Check ECC results for data (if applicable)
+ *  - Check for blank/erased block (return empty ExtendedTags if blank)
+ *  - Check the packed_tags1 mini-ECC (correct if necessary/possible)
+ *  - Convert packed_tags1 to ExtendedTags
+ *  - Update ecc_result and block_bad members to refect state.
+ *
+ * Returns YAFFS_OK or YAFFS_FAIL.
+ */
+int nandmtd1_read_chunk_tags(struct yaffs_dev *dev,
+			     int nand_chunk, u8 * data,
+			     struct yaffs_ext_tags *etags)
+{
+	struct mtd_info *mtd = yaffs_dev_to_mtd(dev);
+	int chunk_bytes = dev->data_bytes_per_chunk;
+	loff_t addr = ((loff_t) nand_chunk) * chunk_bytes;
+	int eccres = YAFFS_ECC_RESULT_NO_ERROR;
+	struct mtd_oob_ops ops;
+	struct yaffs_packed_tags1 pt1;
+	int retval;
+	int deleted;
+
+	memset(&ops, 0, sizeof(ops));
+	ops.mode = MTD_OOB_AUTO;
+	ops.len = (data) ? chunk_bytes : 0;
+	ops.ooblen = YTAG1_SIZE;
+	ops.datbuf = data;
+	ops.oobbuf = (u8 *) & pt1;
+
+	/* Read page and oob using MTD.
+	 * Check status and determine ECC result.
+	 */
+	retval = mtd->read_oob(mtd, addr, &ops);
+	if (retval) {
+		yaffs_trace(YAFFS_TRACE_MTD,
+			"read_oob failed, chunk %d, mtd error %d",
+			nand_chunk, retval);
+	}
+
+	switch (retval) {
+	case 0:
+		/* no error */
+		break;
+
+	case -EUCLEAN:
+		/* MTD's ECC fixed the data */
+		eccres = YAFFS_ECC_RESULT_FIXED;
+		dev->n_ecc_fixed++;
+		break;
+
+	case -EBADMSG:
+		/* MTD's ECC could not fix the data */
+		dev->n_ecc_unfixed++;
+		/* fall into... */
+	default:
+		rettags(etags, YAFFS_ECC_RESULT_UNFIXED, 0);
+		etags->block_bad = (mtd->block_isbad) (mtd, addr);
+		return YAFFS_FAIL;
+	}
+
+	/* Check for a blank/erased chunk.
+	 */
+	if (yaffs_check_ff((u8 *) & pt1, 8)) {
+		/* when blank, upper layers want ecc_result to be <= NO_ERROR */
+		return rettags(etags, YAFFS_ECC_RESULT_NO_ERROR, YAFFS_OK);
+	}
+#ifndef CONFIG_YAFFS_9BYTE_TAGS
+	/* Read deleted status (bit) then return it to it's non-deleted
+	 * state before performing tags mini-ECC check. pt1.deleted is
+	 * inverted.
+	 */
+	deleted = !pt1.deleted;
+	pt1.deleted = 1;
+#else
+	deleted = (yaffs_count_bits(((u8 *) & pt1)[8]) < 7);
+#endif
+
+	/* Check the packed tags mini-ECC and correct if necessary/possible.
+	 */
+	retval = yaffs_check_tags_ecc((struct yaffs_tags *)&pt1);
+	switch (retval) {
+	case 0:
+		/* no tags error, use MTD result */
+		break;
+	case 1:
+		/* recovered tags-ECC error */
+		dev->n_tags_ecc_fixed++;
+		if (eccres == YAFFS_ECC_RESULT_NO_ERROR)
+			eccres = YAFFS_ECC_RESULT_FIXED;
+		break;
+	default:
+		/* unrecovered tags-ECC error */
+		dev->n_tags_ecc_unfixed++;
+		return rettags(etags, YAFFS_ECC_RESULT_UNFIXED, YAFFS_FAIL);
+	}
+
+	/* Unpack the tags to extended form and set ECC result.
+	 * [set should_be_ff just to keep yaffs_unpack_tags1 happy]
+	 */
+	pt1.should_be_ff = 0xFFFFFFFF;
+	yaffs_unpack_tags1(etags, &pt1);
+	etags->ecc_result = eccres;
+
+	/* Set deleted state */
+	etags->is_deleted = deleted;
+	return YAFFS_OK;
+}
+
+/* Mark a block bad.
+ *
+ * This is a persistant state.
+ * Use of this function should be rare.
+ *
+ * Returns YAFFS_OK or YAFFS_FAIL.
+ */
+int nandmtd1_mark_block_bad(struct yaffs_dev *dev, int block_no)
+{
+	struct mtd_info *mtd = yaffs_dev_to_mtd(dev);
+	int blocksize = dev->param.chunks_per_block * dev->data_bytes_per_chunk;
+	int retval;
+
+	yaffs_trace(YAFFS_TRACE_BAD_BLOCKS,
+		"marking block %d bad", block_no);
+
+	retval = mtd->block_markbad(mtd, (loff_t) blocksize * block_no);
+	return (retval) ? YAFFS_FAIL : YAFFS_OK;
+}
+
+/* Check any MTD prerequists.
+ *
+ * Returns YAFFS_OK or YAFFS_FAIL.
+ */
+static int nandmtd1_test_prerequists(struct mtd_info *mtd)
+{
+	/* 2.6.18 has mtd->ecclayout->oobavail */
+	/* 2.6.21 has mtd->ecclayout->oobavail and mtd->oobavail */
+	int oobavail = mtd->ecclayout->oobavail;
+
+	if (oobavail < YTAG1_SIZE) {
+		yaffs_trace(YAFFS_TRACE_ERROR,
+			"mtd device has only %d bytes for tags, need %d",
+			oobavail, YTAG1_SIZE);
+		return YAFFS_FAIL;
+	}
+	return YAFFS_OK;
+}
+
+/* Query for the current state of a specific block.
+ *
+ * Examine the tags of the first chunk of the block and return the state:
+ *  - YAFFS_BLOCK_STATE_DEAD, the block is marked bad
+ *  - YAFFS_BLOCK_STATE_NEEDS_SCANNING, the block is in use
+ *  - YAFFS_BLOCK_STATE_EMPTY, the block is clean
+ *
+ * Always returns YAFFS_OK.
+ */
+int nandmtd1_query_block(struct yaffs_dev *dev, int block_no,
+			 enum yaffs_block_state *state_ptr, u32 * seq_ptr)
+{
+	struct mtd_info *mtd = yaffs_dev_to_mtd(dev);
+	int chunk_num = block_no * dev->param.chunks_per_block;
+	loff_t addr = (loff_t) chunk_num * dev->data_bytes_per_chunk;
+	struct yaffs_ext_tags etags;
+	int state = YAFFS_BLOCK_STATE_DEAD;
+	int seqnum = 0;
+	int retval;
+
+	/* We don't yet have a good place to test for MTD config prerequists.
+	 * Do it here as we are called during the initial scan.
+	 */
+	if (nandmtd1_test_prerequists(mtd) != YAFFS_OK)
+		return YAFFS_FAIL;
+
+	retval = nandmtd1_read_chunk_tags(dev, chunk_num, NULL, &etags);
+	etags.block_bad = (mtd->block_isbad) (mtd, addr);
+	if (etags.block_bad) {
+		yaffs_trace(YAFFS_TRACE_BAD_BLOCKS,
+			"block %d is marked bad", block_no);
+		state = YAFFS_BLOCK_STATE_DEAD;
+	} else if (etags.ecc_result != YAFFS_ECC_RESULT_NO_ERROR) {
+		/* bad tags, need to look more closely */
+		state = YAFFS_BLOCK_STATE_NEEDS_SCANNING;
+	} else if (etags.chunk_used) {
+		state = YAFFS_BLOCK_STATE_NEEDS_SCANNING;
+		seqnum = etags.seq_number;
+	} else {
+		state = YAFFS_BLOCK_STATE_EMPTY;
+	}
+
+	*state_ptr = state;
+	*seq_ptr = seqnum;
+
+	/* query always succeeds */
+	return YAFFS_OK;
+}
diff --git a/fs/yaffs2/yaffs_mtdif1.h b/fs/yaffs2/yaffs_mtdif1.h
new file mode 100644
index 0000000..07ce452
--- /dev/null
+++ b/fs/yaffs2/yaffs_mtdif1.h
@@ -0,0 +1,29 @@
+/*
+ * YAFFS: Yet another Flash File System. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+#ifndef __YAFFS_MTDIF1_H__
+#define __YAFFS_MTDIF1_H__
+
+int nandmtd1_write_chunk_tags(struct yaffs_dev *dev, int nand_chunk,
+			      const u8 * data,
+			      const struct yaffs_ext_tags *tags);
+
+int nandmtd1_read_chunk_tags(struct yaffs_dev *dev, int nand_chunk,
+			     u8 * data, struct yaffs_ext_tags *tags);
+
+int nandmtd1_mark_block_bad(struct yaffs_dev *dev, int block_no);
+
+int nandmtd1_query_block(struct yaffs_dev *dev, int block_no,
+			 enum yaffs_block_state *state, u32 * seq_number);
+
+#endif
diff --git a/fs/yaffs2/yaffs_mtdif2.c b/fs/yaffs2/yaffs_mtdif2.c
new file mode 100644
index 0000000..d1643df
--- /dev/null
+++ b/fs/yaffs2/yaffs_mtdif2.c
@@ -0,0 +1,225 @@
+/*
+ * YAFFS: Yet Another Flash File System. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU General Public License version 2 as
+ * published by the Free Software Foundation.
+ */
+
+/* mtd interface for YAFFS2 */
+
+#include "yportenv.h"
+#include "yaffs_trace.h"
+
+#include "yaffs_mtdif2.h"
+
+#include "linux/mtd/mtd.h"
+#include "linux/types.h"
+#include "linux/time.h"
+
+#include "yaffs_packedtags2.h"
+
+#include "yaffs_linux.h"
+
+/* NB For use with inband tags....
+ * We assume that the data buffer is of size total_bytes_per_chunk so that we can also
+ * use it to load the tags.
+ */
+int nandmtd2_write_chunk_tags(struct yaffs_dev *dev, int nand_chunk,
+			      const u8 * data,
+			      const struct yaffs_ext_tags *tags)
+{
+	struct mtd_info *mtd = yaffs_dev_to_mtd(dev);
+	struct mtd_oob_ops ops;
+	int retval = 0;
+
+	loff_t addr;
+
+	struct yaffs_packed_tags2 pt;
+
+	int packed_tags_size =
+	    dev->param.no_tags_ecc ? sizeof(pt.t) : sizeof(pt);
+	void *packed_tags_ptr =
+	    dev->param.no_tags_ecc ? (void *)&pt.t : (void *)&pt;
+
+	yaffs_trace(YAFFS_TRACE_MTD,
+		"nandmtd2_write_chunk_tags chunk %d data %p tags %p",
+		nand_chunk, data, tags);
+
+	addr = ((loff_t) nand_chunk) * dev->param.total_bytes_per_chunk;
+
+	/* For yaffs2 writing there must be both data and tags.
+	 * If we're using inband tags, then the tags are stuffed into
+	 * the end of the data buffer.
+	 */
+	if (!data || !tags)
+		BUG();
+	else if (dev->param.inband_tags) {
+		struct yaffs_packed_tags2_tags_only *pt2tp;
+		pt2tp =
+		    (struct yaffs_packed_tags2_tags_only *)(data +
+							    dev->
+							    data_bytes_per_chunk);
+		yaffs_pack_tags2_tags_only(pt2tp, tags);
+	} else {
+		yaffs_pack_tags2(&pt, tags, !dev->param.no_tags_ecc);
+        }
+
+	ops.mode = MTD_OOB_AUTO;
+	ops.ooblen = (dev->param.inband_tags) ? 0 : packed_tags_size;
+	ops.len = dev->param.total_bytes_per_chunk;
+	ops.ooboffs = 0;
+	ops.datbuf = (u8 *) data;
+	ops.oobbuf = (dev->param.inband_tags) ? NULL : packed_tags_ptr;
+	retval = mtd->write_oob(mtd, addr, &ops);
+
+	if (retval == 0)
+		return YAFFS_OK;
+	else
+		return YAFFS_FAIL;
+}
+
+int nandmtd2_read_chunk_tags(struct yaffs_dev *dev, int nand_chunk,
+			     u8 * data, struct yaffs_ext_tags *tags)
+{
+	struct mtd_info *mtd = yaffs_dev_to_mtd(dev);
+	struct mtd_oob_ops ops;
+
+	size_t dummy;
+	int retval = 0;
+	int local_data = 0;
+
+	loff_t addr = ((loff_t) nand_chunk) * dev->param.total_bytes_per_chunk;
+
+	struct yaffs_packed_tags2 pt;
+
+	int packed_tags_size =
+	    dev->param.no_tags_ecc ? sizeof(pt.t) : sizeof(pt);
+	void *packed_tags_ptr =
+	    dev->param.no_tags_ecc ? (void *)&pt.t : (void *)&pt;
+
+	yaffs_trace(YAFFS_TRACE_MTD,
+		"nandmtd2_read_chunk_tags chunk %d data %p tags %p",
+		nand_chunk, data, tags);
+
+	if (dev->param.inband_tags) {
+
+		if (!data) {
+			local_data = 1;
+			data = yaffs_get_temp_buffer(dev, __LINE__);
+		}
+
+	}
+
+	if (dev->param.inband_tags || (data && !tags))
+		retval = mtd->read(mtd, addr, dev->param.total_bytes_per_chunk,
+				   &dummy, data);
+	else if (tags) {
+		ops.mode = MTD_OOB_AUTO;
+		ops.ooblen = packed_tags_size;
+		ops.len = data ? dev->data_bytes_per_chunk : packed_tags_size;
+		ops.ooboffs = 0;
+		ops.datbuf = data;
+		ops.oobbuf = yaffs_dev_to_lc(dev)->spare_buffer;
+		retval = mtd->read_oob(mtd, addr, &ops);
+	}
+
+	if (dev->param.inband_tags) {
+		if (tags) {
+			struct yaffs_packed_tags2_tags_only *pt2tp;
+			pt2tp =
+			    (struct yaffs_packed_tags2_tags_only *)&data[dev->
+									 data_bytes_per_chunk];
+			yaffs_unpack_tags2_tags_only(tags, pt2tp);
+		}
+	} else {
+		if (tags) {
+			memcpy(packed_tags_ptr,
+			       yaffs_dev_to_lc(dev)->spare_buffer,
+			       packed_tags_size);
+			yaffs_unpack_tags2(tags, &pt, !dev->param.no_tags_ecc);
+		}
+	}
+
+	if (local_data)
+		yaffs_release_temp_buffer(dev, data, __LINE__);
+
+	if (tags && retval == -EBADMSG
+	    && tags->ecc_result == YAFFS_ECC_RESULT_NO_ERROR) {
+		tags->ecc_result = YAFFS_ECC_RESULT_UNFIXED;
+		dev->n_ecc_unfixed++;
+	}
+	if (tags && retval == -EUCLEAN
+	    && tags->ecc_result == YAFFS_ECC_RESULT_NO_ERROR) {
+		tags->ecc_result = YAFFS_ECC_RESULT_FIXED;
+		dev->n_ecc_fixed++;
+	}
+	if (retval == 0)
+		return YAFFS_OK;
+	else
+		return YAFFS_FAIL;
+}
+
+int nandmtd2_mark_block_bad(struct yaffs_dev *dev, int block_no)
+{
+	struct mtd_info *mtd = yaffs_dev_to_mtd(dev);
+	int retval;
+	yaffs_trace(YAFFS_TRACE_MTD,
+		"nandmtd2_mark_block_bad %d", block_no);
+
+	retval =
+	    mtd->block_markbad(mtd,
+			       block_no * dev->param.chunks_per_block *
+			       dev->param.total_bytes_per_chunk);
+
+	if (retval == 0)
+		return YAFFS_OK;
+	else
+		return YAFFS_FAIL;
+
+}
+
+int nandmtd2_query_block(struct yaffs_dev *dev, int block_no,
+			 enum yaffs_block_state *state, u32 * seq_number)
+{
+	struct mtd_info *mtd = yaffs_dev_to_mtd(dev);
+	int retval;
+
+	yaffs_trace(YAFFS_TRACE_MTD, "nandmtd2_query_block %d", block_no);
+	retval =
+	    mtd->block_isbad(mtd,
+			     block_no * dev->param.chunks_per_block *
+			     dev->param.total_bytes_per_chunk);
+
+	if (retval) {
+		yaffs_trace(YAFFS_TRACE_MTD, "block is bad");
+
+		*state = YAFFS_BLOCK_STATE_DEAD;
+		*seq_number = 0;
+	} else {
+		struct yaffs_ext_tags t;
+		nandmtd2_read_chunk_tags(dev, block_no *
+					 dev->param.chunks_per_block, NULL, &t);
+
+		if (t.chunk_used) {
+			*seq_number = t.seq_number;
+			*state = YAFFS_BLOCK_STATE_NEEDS_SCANNING;
+		} else {
+			*seq_number = 0;
+			*state = YAFFS_BLOCK_STATE_EMPTY;
+		}
+	}
+	yaffs_trace(YAFFS_TRACE_MTD,
+		"block is bad seq %d state %d", *seq_number, *state);
+
+	if (retval == 0)
+		return YAFFS_OK;
+	else
+		return YAFFS_FAIL;
+}
+
diff --git a/fs/yaffs2/yaffs_mtdif2.h b/fs/yaffs2/yaffs_mtdif2.h
new file mode 100644
index 0000000..d821126
--- /dev/null
+++ b/fs/yaffs2/yaffs_mtdif2.h
@@ -0,0 +1,29 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+#ifndef __YAFFS_MTDIF2_H__
+#define __YAFFS_MTDIF2_H__
+
+#include "yaffs_guts.h"
+int nandmtd2_write_chunk_tags(struct yaffs_dev *dev, int nand_chunk,
+			      const u8 * data,
+			      const struct yaffs_ext_tags *tags);
+int nandmtd2_read_chunk_tags(struct yaffs_dev *dev, int nand_chunk,
+			     u8 * data, struct yaffs_ext_tags *tags);
+int nandmtd2_mark_block_bad(struct yaffs_dev *dev, int block_no);
+int nandmtd2_query_block(struct yaffs_dev *dev, int block_no,
+			 enum yaffs_block_state *state, u32 * seq_number);
+
+#endif
diff --git a/fs/yaffs2/yaffs_nameval.c b/fs/yaffs2/yaffs_nameval.c
new file mode 100644
index 0000000..daa36f9
--- /dev/null
+++ b/fs/yaffs2/yaffs_nameval.c
@@ -0,0 +1,201 @@
+/*
+ * YAFFS: Yet Another Flash File System. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU General Public License version 2 as
+ * published by the Free Software Foundation.
+ */
+
+/*
+ * This simple implementation of a name-value store assumes a small number of values and fits
+ * into a small finite buffer.
+ *
+ * Each attribute is stored as a record:
+ *  sizeof(int) bytes   record size.
+ *  strnlen+1 bytes name null terminated.
+ *  nbytes    value.
+ *  ----------
+ *  total size  stored in record size 
+ *
+ * This code has not been tested with unicode yet.
+ */
+
+#include "yaffs_nameval.h"
+
+#include "yportenv.h"
+
+static int nval_find(const char *xb, int xb_size, const YCHAR * name,
+		     int *exist_size)
+{
+	int pos = 0;
+	int size;
+
+	memcpy(&size, xb, sizeof(int));
+	while (size > 0 && (size < xb_size) && (pos + size < xb_size)) {
+		if (strncmp
+		    ((YCHAR *) (xb + pos + sizeof(int)), name, size) == 0) {
+			if (exist_size)
+				*exist_size = size;
+			return pos;
+		}
+		pos += size;
+		if (pos < xb_size - sizeof(int))
+			memcpy(&size, xb + pos, sizeof(int));
+		else
+			size = 0;
+	}
+	if (exist_size)
+		*exist_size = 0;
+	return -1;
+}
+
+static int nval_used(const char *xb, int xb_size)
+{
+	int pos = 0;
+	int size;
+
+	memcpy(&size, xb + pos, sizeof(int));
+	while (size > 0 && (size < xb_size) && (pos + size < xb_size)) {
+		pos += size;
+		if (pos < xb_size - sizeof(int))
+			memcpy(&size, xb + pos, sizeof(int));
+		else
+			size = 0;
+	}
+	return pos;
+}
+
+int nval_del(char *xb, int xb_size, const YCHAR * name)
+{
+	int pos = nval_find(xb, xb_size, name, NULL);
+	int size;
+
+	if (pos >= 0 && pos < xb_size) {
+		/* Find size, shift rest over this record, then zero out the rest of buffer */
+		memcpy(&size, xb + pos, sizeof(int));
+		memcpy(xb + pos, xb + pos + size, xb_size - (pos + size));
+		memset(xb + (xb_size - size), 0, size);
+		return 0;
+	} else {
+		return -ENODATA;
+        }
+}
+
+int nval_set(char *xb, int xb_size, const YCHAR * name, const char *buf,
+	     int bsize, int flags)
+{
+	int pos;
+	int namelen = strnlen(name, xb_size);
+	int reclen;
+	int size_exist = 0;
+	int space;
+	int start;
+
+	pos = nval_find(xb, xb_size, name, &size_exist);
+
+	if (flags & XATTR_CREATE && pos >= 0)
+		return -EEXIST;
+	if (flags & XATTR_REPLACE && pos < 0)
+		return -ENODATA;
+
+	start = nval_used(xb, xb_size);
+	space = xb_size - start + size_exist;
+
+	reclen = (sizeof(int) + namelen + 1 + bsize);
+
+	if (reclen > space)
+		return -ENOSPC;
+
+	if (pos >= 0) {
+		nval_del(xb, xb_size, name);
+		start = nval_used(xb, xb_size);
+	}
+
+	pos = start;
+
+	memcpy(xb + pos, &reclen, sizeof(int));
+	pos += sizeof(int);
+	strncpy((YCHAR *) (xb + pos), name, reclen);
+	pos += (namelen + 1);
+	memcpy(xb + pos, buf, bsize);
+	return 0;
+}
+
+int nval_get(const char *xb, int xb_size, const YCHAR * name, char *buf,
+	     int bsize)
+{
+	int pos = nval_find(xb, xb_size, name, NULL);
+	int size;
+
+	if (pos >= 0 && pos < xb_size) {
+
+		memcpy(&size, xb + pos, sizeof(int));
+		pos += sizeof(int);	/* advance past record length */
+		size -= sizeof(int);
+
+		/* Advance over name string */
+		while (xb[pos] && size > 0 && pos < xb_size) {
+			pos++;
+			size--;
+		}
+		/*Advance over NUL */
+		pos++;
+		size--;
+
+		if (size <= bsize) {
+			memcpy(buf, xb + pos, size);
+			return size;
+		}
+
+	}
+	if (pos >= 0)
+		return -ERANGE;
+	else
+		return -ENODATA;
+}
+
+int nval_list(const char *xb, int xb_size, char *buf, int bsize)
+{
+	int pos = 0;
+	int size;
+	int name_len;
+	int ncopied = 0;
+	int filled = 0;
+
+	memcpy(&size, xb + pos, sizeof(int));
+	while (size > sizeof(int) && size <= xb_size && (pos + size) < xb_size
+	       && !filled) {
+		pos += sizeof(int);
+		size -= sizeof(int);
+		name_len = strnlen((YCHAR *) (xb + pos), size);
+		if (ncopied + name_len + 1 < bsize) {
+			memcpy(buf, xb + pos, name_len * sizeof(YCHAR));
+			buf += name_len;
+			*buf = '\0';
+			buf++;
+			if (sizeof(YCHAR) > 1) {
+				*buf = '\0';
+				buf++;
+			}
+			ncopied += (name_len + 1);
+		} else {
+			filled = 1;
+                }
+		pos += size;
+		if (pos < xb_size - sizeof(int))
+			memcpy(&size, xb + pos, sizeof(int));
+		else
+			size = 0;
+	}
+	return ncopied;
+}
+
+int nval_hasvalues(const char *xb, int xb_size)
+{
+	return nval_used(xb, xb_size) > 0;
+}
diff --git a/fs/yaffs2/yaffs_nameval.h b/fs/yaffs2/yaffs_nameval.h
new file mode 100644
index 0000000..2bb02b6
--- /dev/null
+++ b/fs/yaffs2/yaffs_nameval.h
@@ -0,0 +1,28 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+#ifndef __NAMEVAL_H__
+#define __NAMEVAL_H__
+
+#include "yportenv.h"
+
+int nval_del(char *xb, int xb_size, const YCHAR * name);
+int nval_set(char *xb, int xb_size, const YCHAR * name, const char *buf,
+	     int bsize, int flags);
+int nval_get(const char *xb, int xb_size, const YCHAR * name, char *buf,
+	     int bsize);
+int nval_list(const char *xb, int xb_size, char *buf, int bsize);
+int nval_hasvalues(const char *xb, int xb_size);
+#endif
diff --git a/fs/yaffs2/yaffs_nand.c b/fs/yaffs2/yaffs_nand.c
new file mode 100644
index 0000000..e816cab
--- /dev/null
+++ b/fs/yaffs2/yaffs_nand.c
@@ -0,0 +1,127 @@
+/*
+ * YAFFS: Yet Another Flash File System. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU General Public License version 2 as
+ * published by the Free Software Foundation.
+ */
+
+#include "yaffs_nand.h"
+#include "yaffs_tagscompat.h"
+#include "yaffs_tagsvalidity.h"
+
+#include "yaffs_getblockinfo.h"
+
+int yaffs_rd_chunk_tags_nand(struct yaffs_dev *dev, int nand_chunk,
+			     u8 * buffer, struct yaffs_ext_tags *tags)
+{
+	int result;
+	struct yaffs_ext_tags local_tags;
+
+	int realigned_chunk = nand_chunk - dev->chunk_offset;
+
+	dev->n_page_reads++;
+
+	/* If there are no tags provided, use local tags to get prioritised gc working */
+	if (!tags)
+		tags = &local_tags;
+
+	if (dev->param.read_chunk_tags_fn)
+		result =
+		    dev->param.read_chunk_tags_fn(dev, realigned_chunk, buffer,
+						  tags);
+	else
+		result = yaffs_tags_compat_rd(dev,
+					      realigned_chunk, buffer, tags);
+	if (tags && tags->ecc_result > YAFFS_ECC_RESULT_NO_ERROR) {
+
+		struct yaffs_block_info *bi;
+		bi = yaffs_get_block_info(dev,
+					  nand_chunk /
+					  dev->param.chunks_per_block);
+		yaffs_handle_chunk_error(dev, bi);
+	}
+
+	return result;
+}
+
+int yaffs_wr_chunk_tags_nand(struct yaffs_dev *dev,
+			     int nand_chunk,
+			     const u8 * buffer, struct yaffs_ext_tags *tags)
+{
+
+	dev->n_page_writes++;
+
+	nand_chunk -= dev->chunk_offset;
+
+	if (tags) {
+		tags->seq_number = dev->seq_number;
+		tags->chunk_used = 1;
+		if (!yaffs_validate_tags(tags)) {
+			yaffs_trace(YAFFS_TRACE_ERROR, "Writing uninitialised tags");
+			YBUG();
+		}
+		yaffs_trace(YAFFS_TRACE_WRITE,
+			"Writing chunk %d tags %d %d",
+			nand_chunk, tags->obj_id, tags->chunk_id);
+	} else {
+		yaffs_trace(YAFFS_TRACE_ERROR, "Writing with no tags");
+		YBUG();
+	}
+
+	if (dev->param.write_chunk_tags_fn)
+		return dev->param.write_chunk_tags_fn(dev, nand_chunk, buffer,
+						      tags);
+	else
+		return yaffs_tags_compat_wr(dev, nand_chunk, buffer, tags);
+}
+
+int yaffs_mark_bad(struct yaffs_dev *dev, int block_no)
+{
+	block_no -= dev->block_offset;
+
+	if (dev->param.bad_block_fn)
+		return dev->param.bad_block_fn(dev, block_no);
+	else
+		return yaffs_tags_compat_mark_bad(dev, block_no);
+}
+
+int yaffs_query_init_block_state(struct yaffs_dev *dev,
+				 int block_no,
+				 enum yaffs_block_state *state,
+				 u32 * seq_number)
+{
+	block_no -= dev->block_offset;
+
+	if (dev->param.query_block_fn)
+		return dev->param.query_block_fn(dev, block_no, state,
+						 seq_number);
+	else
+		return yaffs_tags_compat_query_block(dev, block_no,
+						     state, seq_number);
+}
+
+int yaffs_erase_block(struct yaffs_dev *dev, int flash_block)
+{
+	int result;
+
+	flash_block -= dev->block_offset;
+
+	dev->n_erasures++;
+
+	result = dev->param.erase_fn(dev, flash_block);
+
+	return result;
+}
+
+int yaffs_init_nand(struct yaffs_dev *dev)
+{
+	if (dev->param.initialise_flash_fn)
+		return dev->param.initialise_flash_fn(dev);
+	return YAFFS_OK;
+}
diff --git a/fs/yaffs2/yaffs_nand.h b/fs/yaffs2/yaffs_nand.h
new file mode 100644
index 0000000..543f198
--- /dev/null
+++ b/fs/yaffs2/yaffs_nand.h
@@ -0,0 +1,38 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+#ifndef __YAFFS_NAND_H__
+#define __YAFFS_NAND_H__
+#include "yaffs_guts.h"
+
+int yaffs_rd_chunk_tags_nand(struct yaffs_dev *dev, int nand_chunk,
+			     u8 * buffer, struct yaffs_ext_tags *tags);
+
+int yaffs_wr_chunk_tags_nand(struct yaffs_dev *dev,
+			     int nand_chunk,
+			     const u8 * buffer, struct yaffs_ext_tags *tags);
+
+int yaffs_mark_bad(struct yaffs_dev *dev, int block_no);
+
+int yaffs_query_init_block_state(struct yaffs_dev *dev,
+				 int block_no,
+				 enum yaffs_block_state *state,
+				 unsigned *seq_number);
+
+int yaffs_erase_block(struct yaffs_dev *dev, int flash_block);
+
+int yaffs_init_nand(struct yaffs_dev *dev);
+
+#endif
diff --git a/fs/yaffs2/yaffs_packedtags1.c b/fs/yaffs2/yaffs_packedtags1.c
new file mode 100644
index 0000000..a77f095
--- /dev/null
+++ b/fs/yaffs2/yaffs_packedtags1.c
@@ -0,0 +1,53 @@
+/*
+ * YAFFS: Yet Another Flash File System. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU General Public License version 2 as
+ * published by the Free Software Foundation.
+ */
+
+#include "yaffs_packedtags1.h"
+#include "yportenv.h"
+
+void yaffs_pack_tags1(struct yaffs_packed_tags1 *pt,
+		      const struct yaffs_ext_tags *t)
+{
+	pt->chunk_id = t->chunk_id;
+	pt->serial_number = t->serial_number;
+	pt->n_bytes = t->n_bytes;
+	pt->obj_id = t->obj_id;
+	pt->ecc = 0;
+	pt->deleted = (t->is_deleted) ? 0 : 1;
+	pt->unused_stuff = 0;
+	pt->should_be_ff = 0xFFFFFFFF;
+
+}
+
+void yaffs_unpack_tags1(struct yaffs_ext_tags *t,
+			const struct yaffs_packed_tags1 *pt)
+{
+	static const u8 all_ff[] =
+	    { 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff,
+		0xff
+	};
+
+	if (memcmp(all_ff, pt, sizeof(struct yaffs_packed_tags1))) {
+		t->block_bad = 0;
+		if (pt->should_be_ff != 0xFFFFFFFF)
+			t->block_bad = 1;
+		t->chunk_used = 1;
+		t->obj_id = pt->obj_id;
+		t->chunk_id = pt->chunk_id;
+		t->n_bytes = pt->n_bytes;
+		t->ecc_result = YAFFS_ECC_RESULT_NO_ERROR;
+		t->is_deleted = (pt->deleted) ? 0 : 1;
+		t->serial_number = pt->serial_number;
+	} else {
+		memset(t, 0, sizeof(struct yaffs_ext_tags));
+	}
+}
diff --git a/fs/yaffs2/yaffs_packedtags1.h b/fs/yaffs2/yaffs_packedtags1.h
new file mode 100644
index 0000000..d6861ff
--- /dev/null
+++ b/fs/yaffs2/yaffs_packedtags1.h
@@ -0,0 +1,39 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+/* This is used to pack YAFFS1 tags, not YAFFS2 tags. */
+
+#ifndef __YAFFS_PACKEDTAGS1_H__
+#define __YAFFS_PACKEDTAGS1_H__
+
+#include "yaffs_guts.h"
+
+struct yaffs_packed_tags1 {
+	unsigned chunk_id:20;
+	unsigned serial_number:2;
+	unsigned n_bytes:10;
+	unsigned obj_id:18;
+	unsigned ecc:12;
+	unsigned deleted:1;
+	unsigned unused_stuff:1;
+	unsigned should_be_ff;
+
+};
+
+void yaffs_pack_tags1(struct yaffs_packed_tags1 *pt,
+		      const struct yaffs_ext_tags *t);
+void yaffs_unpack_tags1(struct yaffs_ext_tags *t,
+			const struct yaffs_packed_tags1 *pt);
+#endif
diff --git a/fs/yaffs2/yaffs_packedtags2.c b/fs/yaffs2/yaffs_packedtags2.c
new file mode 100644
index 0000000..8e7fea3
--- /dev/null
+++ b/fs/yaffs2/yaffs_packedtags2.c
@@ -0,0 +1,196 @@
+/*
+ * YAFFS: Yet Another Flash File System. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU General Public License version 2 as
+ * published by the Free Software Foundation.
+ */
+
+#include "yaffs_packedtags2.h"
+#include "yportenv.h"
+#include "yaffs_trace.h"
+#include "yaffs_tagsvalidity.h"
+
+/* This code packs a set of extended tags into a binary structure for
+ * NAND storage
+ */
+
+/* Some of the information is "extra" struff which can be packed in to
+ * speed scanning
+ * This is defined by having the EXTRA_HEADER_INFO_FLAG set.
+ */
+
+/* Extra flags applied to chunk_id */
+
+#define EXTRA_HEADER_INFO_FLAG	0x80000000
+#define EXTRA_SHRINK_FLAG	0x40000000
+#define EXTRA_SHADOWS_FLAG	0x20000000
+#define EXTRA_SPARE_FLAGS	0x10000000
+
+#define ALL_EXTRA_FLAGS		0xF0000000
+
+/* Also, the top 4 bits of the object Id are set to the object type. */
+#define EXTRA_OBJECT_TYPE_SHIFT (28)
+#define EXTRA_OBJECT_TYPE_MASK  ((0x0F) << EXTRA_OBJECT_TYPE_SHIFT)
+
+static void yaffs_dump_packed_tags2_tags_only(const struct
+					      yaffs_packed_tags2_tags_only *ptt)
+{
+	yaffs_trace(YAFFS_TRACE_MTD,
+		"packed tags obj %d chunk %d byte %d seq %d",
+		ptt->obj_id, ptt->chunk_id, ptt->n_bytes, ptt->seq_number);
+}
+
+static void yaffs_dump_packed_tags2(const struct yaffs_packed_tags2 *pt)
+{
+	yaffs_dump_packed_tags2_tags_only(&pt->t);
+}
+
+static void yaffs_dump_tags2(const struct yaffs_ext_tags *t)
+{
+	yaffs_trace(YAFFS_TRACE_MTD,
+		"ext.tags eccres %d blkbad %d chused %d obj %d chunk%d byte %d del %d ser %d seq %d",
+		t->ecc_result, t->block_bad, t->chunk_used, t->obj_id,
+		t->chunk_id, t->n_bytes, t->is_deleted, t->serial_number,
+		t->seq_number);
+
+}
+
+void yaffs_pack_tags2_tags_only(struct yaffs_packed_tags2_tags_only *ptt,
+				const struct yaffs_ext_tags *t)
+{
+	ptt->chunk_id = t->chunk_id;
+	ptt->seq_number = t->seq_number;
+	ptt->n_bytes = t->n_bytes;
+	ptt->obj_id = t->obj_id;
+
+	if (t->chunk_id == 0 && t->extra_available) {
+		/* Store the extra header info instead */
+		/* We save the parent object in the chunk_id */
+		ptt->chunk_id = EXTRA_HEADER_INFO_FLAG | t->extra_parent_id;
+		if (t->extra_is_shrink)
+			ptt->chunk_id |= EXTRA_SHRINK_FLAG;
+		if (t->extra_shadows)
+			ptt->chunk_id |= EXTRA_SHADOWS_FLAG;
+
+		ptt->obj_id &= ~EXTRA_OBJECT_TYPE_MASK;
+		ptt->obj_id |= (t->extra_obj_type << EXTRA_OBJECT_TYPE_SHIFT);
+
+		if (t->extra_obj_type == YAFFS_OBJECT_TYPE_HARDLINK)
+			ptt->n_bytes = t->extra_equiv_id;
+		else if (t->extra_obj_type == YAFFS_OBJECT_TYPE_FILE)
+			ptt->n_bytes = t->extra_length;
+		else
+			ptt->n_bytes = 0;
+	}
+
+	yaffs_dump_packed_tags2_tags_only(ptt);
+	yaffs_dump_tags2(t);
+}
+
+void yaffs_pack_tags2(struct yaffs_packed_tags2 *pt,
+		      const struct yaffs_ext_tags *t, int tags_ecc)
+{
+	yaffs_pack_tags2_tags_only(&pt->t, t);
+
+	if (tags_ecc)
+		yaffs_ecc_calc_other((unsigned char *)&pt->t,
+				     sizeof(struct
+					    yaffs_packed_tags2_tags_only),
+				     &pt->ecc);
+}
+
+void yaffs_unpack_tags2_tags_only(struct yaffs_ext_tags *t,
+				  struct yaffs_packed_tags2_tags_only *ptt)
+{
+
+	memset(t, 0, sizeof(struct yaffs_ext_tags));
+
+	yaffs_init_tags(t);
+
+	if (ptt->seq_number != 0xFFFFFFFF) {
+		t->block_bad = 0;
+		t->chunk_used = 1;
+		t->obj_id = ptt->obj_id;
+		t->chunk_id = ptt->chunk_id;
+		t->n_bytes = ptt->n_bytes;
+		t->is_deleted = 0;
+		t->serial_number = 0;
+		t->seq_number = ptt->seq_number;
+
+		/* Do extra header info stuff */
+
+		if (ptt->chunk_id & EXTRA_HEADER_INFO_FLAG) {
+			t->chunk_id = 0;
+			t->n_bytes = 0;
+
+			t->extra_available = 1;
+			t->extra_parent_id =
+			    ptt->chunk_id & (~(ALL_EXTRA_FLAGS));
+			t->extra_is_shrink =
+			    (ptt->chunk_id & EXTRA_SHRINK_FLAG) ? 1 : 0;
+			t->extra_shadows =
+			    (ptt->chunk_id & EXTRA_SHADOWS_FLAG) ? 1 : 0;
+			t->extra_obj_type =
+			    ptt->obj_id >> EXTRA_OBJECT_TYPE_SHIFT;
+			t->obj_id &= ~EXTRA_OBJECT_TYPE_MASK;
+
+			if (t->extra_obj_type == YAFFS_OBJECT_TYPE_HARDLINK)
+				t->extra_equiv_id = ptt->n_bytes;
+			else
+				t->extra_length = ptt->n_bytes;
+		}
+	}
+
+	yaffs_dump_packed_tags2_tags_only(ptt);
+	yaffs_dump_tags2(t);
+
+}
+
+void yaffs_unpack_tags2(struct yaffs_ext_tags *t, struct yaffs_packed_tags2 *pt,
+			int tags_ecc)
+{
+
+	enum yaffs_ecc_result ecc_result = YAFFS_ECC_RESULT_NO_ERROR;
+
+	if (pt->t.seq_number != 0xFFFFFFFF && tags_ecc) {
+		/* Chunk is in use and we need to do ECC */
+
+		struct yaffs_ecc_other ecc;
+		int result;
+		yaffs_ecc_calc_other((unsigned char *)&pt->t,
+				     sizeof(struct
+					    yaffs_packed_tags2_tags_only),
+				     &ecc);
+		result =
+		    yaffs_ecc_correct_other((unsigned char *)&pt->t,
+					    sizeof(struct
+						   yaffs_packed_tags2_tags_only),
+					    &pt->ecc, &ecc);
+		switch (result) {
+		case 0:
+			ecc_result = YAFFS_ECC_RESULT_NO_ERROR;
+			break;
+		case 1:
+			ecc_result = YAFFS_ECC_RESULT_FIXED;
+			break;
+		case -1:
+			ecc_result = YAFFS_ECC_RESULT_UNFIXED;
+			break;
+		default:
+			ecc_result = YAFFS_ECC_RESULT_UNKNOWN;
+		}
+	}
+
+	yaffs_unpack_tags2_tags_only(t, &pt->t);
+
+	t->ecc_result = ecc_result;
+
+	yaffs_dump_packed_tags2(pt);
+	yaffs_dump_tags2(t);
+}
diff --git a/fs/yaffs2/yaffs_packedtags2.h b/fs/yaffs2/yaffs_packedtags2.h
new file mode 100644
index 0000000..f329669
--- /dev/null
+++ b/fs/yaffs2/yaffs_packedtags2.h
@@ -0,0 +1,47 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+/* This is used to pack YAFFS2 tags, not YAFFS1tags. */
+
+#ifndef __YAFFS_PACKEDTAGS2_H__
+#define __YAFFS_PACKEDTAGS2_H__
+
+#include "yaffs_guts.h"
+#include "yaffs_ecc.h"
+
+struct yaffs_packed_tags2_tags_only {
+	unsigned seq_number;
+	unsigned obj_id;
+	unsigned chunk_id;
+	unsigned n_bytes;
+};
+
+struct yaffs_packed_tags2 {
+	struct yaffs_packed_tags2_tags_only t;
+	struct yaffs_ecc_other ecc;
+};
+
+/* Full packed tags with ECC, used for oob tags */
+void yaffs_pack_tags2(struct yaffs_packed_tags2 *pt,
+		      const struct yaffs_ext_tags *t, int tags_ecc);
+void yaffs_unpack_tags2(struct yaffs_ext_tags *t, struct yaffs_packed_tags2 *pt,
+			int tags_ecc);
+
+/* Only the tags part (no ECC for use with inband tags */
+void yaffs_pack_tags2_tags_only(struct yaffs_packed_tags2_tags_only *pt,
+				const struct yaffs_ext_tags *t);
+void yaffs_unpack_tags2_tags_only(struct yaffs_ext_tags *t,
+				  struct yaffs_packed_tags2_tags_only *pt);
+#endif
diff --git a/fs/yaffs2/yaffs_tagscompat.c b/fs/yaffs2/yaffs_tagscompat.c
new file mode 100644
index 0000000..7578075
--- /dev/null
+++ b/fs/yaffs2/yaffs_tagscompat.c
@@ -0,0 +1,422 @@
+/*
+ * YAFFS: Yet Another Flash File System. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU General Public License version 2 as
+ * published by the Free Software Foundation.
+ */
+
+#include "yaffs_guts.h"
+#include "yaffs_tagscompat.h"
+#include "yaffs_ecc.h"
+#include "yaffs_getblockinfo.h"
+#include "yaffs_trace.h"
+
+static void yaffs_handle_rd_data_error(struct yaffs_dev *dev, int nand_chunk);
+
+
+/********** Tags ECC calculations  *********/
+
+void yaffs_calc_ecc(const u8 * data, struct yaffs_spare *spare)
+{
+	yaffs_ecc_cacl(data, spare->ecc1);
+	yaffs_ecc_cacl(&data[256], spare->ecc2);
+}
+
+void yaffs_calc_tags_ecc(struct yaffs_tags *tags)
+{
+	/* Calculate an ecc */
+
+	unsigned char *b = ((union yaffs_tags_union *)tags)->as_bytes;
+	unsigned i, j;
+	unsigned ecc = 0;
+	unsigned bit = 0;
+
+	tags->ecc = 0;
+
+	for (i = 0; i < 8; i++) {
+		for (j = 1; j & 0xff; j <<= 1) {
+			bit++;
+			if (b[i] & j)
+				ecc ^= bit;
+		}
+	}
+
+	tags->ecc = ecc;
+
+}
+
+int yaffs_check_tags_ecc(struct yaffs_tags *tags)
+{
+	unsigned ecc = tags->ecc;
+
+	yaffs_calc_tags_ecc(tags);
+
+	ecc ^= tags->ecc;
+
+	if (ecc && ecc <= 64) {
+		/* TODO: Handle the failure better. Retire? */
+		unsigned char *b = ((union yaffs_tags_union *)tags)->as_bytes;
+
+		ecc--;
+
+		b[ecc / 8] ^= (1 << (ecc & 7));
+
+		/* Now recvalc the ecc */
+		yaffs_calc_tags_ecc(tags);
+
+		return 1;	/* recovered error */
+	} else if (ecc) {
+		/* Wierd ecc failure value */
+		/* TODO Need to do somethiong here */
+		return -1;	/* unrecovered error */
+	}
+
+	return 0;
+}
+
+/********** Tags **********/
+
+static void yaffs_load_tags_to_spare(struct yaffs_spare *spare_ptr,
+				     struct yaffs_tags *tags_ptr)
+{
+	union yaffs_tags_union *tu = (union yaffs_tags_union *)tags_ptr;
+
+	yaffs_calc_tags_ecc(tags_ptr);
+
+	spare_ptr->tb0 = tu->as_bytes[0];
+	spare_ptr->tb1 = tu->as_bytes[1];
+	spare_ptr->tb2 = tu->as_bytes[2];
+	spare_ptr->tb3 = tu->as_bytes[3];
+	spare_ptr->tb4 = tu->as_bytes[4];
+	spare_ptr->tb5 = tu->as_bytes[5];
+	spare_ptr->tb6 = tu->as_bytes[6];
+	spare_ptr->tb7 = tu->as_bytes[7];
+}
+
+static void yaffs_get_tags_from_spare(struct yaffs_dev *dev,
+				      struct yaffs_spare *spare_ptr,
+				      struct yaffs_tags *tags_ptr)
+{
+	union yaffs_tags_union *tu = (union yaffs_tags_union *)tags_ptr;
+	int result;
+
+	tu->as_bytes[0] = spare_ptr->tb0;
+	tu->as_bytes[1] = spare_ptr->tb1;
+	tu->as_bytes[2] = spare_ptr->tb2;
+	tu->as_bytes[3] = spare_ptr->tb3;
+	tu->as_bytes[4] = spare_ptr->tb4;
+	tu->as_bytes[5] = spare_ptr->tb5;
+	tu->as_bytes[6] = spare_ptr->tb6;
+	tu->as_bytes[7] = spare_ptr->tb7;
+
+	result = yaffs_check_tags_ecc(tags_ptr);
+	if (result > 0)
+		dev->n_tags_ecc_fixed++;
+	else if (result < 0)
+		dev->n_tags_ecc_unfixed++;
+}
+
+static void yaffs_spare_init(struct yaffs_spare *spare)
+{
+	memset(spare, 0xFF, sizeof(struct yaffs_spare));
+}
+
+static int yaffs_wr_nand(struct yaffs_dev *dev,
+			 int nand_chunk, const u8 * data,
+			 struct yaffs_spare *spare)
+{
+	if (nand_chunk < dev->param.start_block * dev->param.chunks_per_block) {
+		yaffs_trace(YAFFS_TRACE_ERROR,
+			"**>> yaffs chunk %d is not valid",
+			nand_chunk);
+		return YAFFS_FAIL;
+	}
+
+	return dev->param.write_chunk_fn(dev, nand_chunk, data, spare);
+}
+
+static int yaffs_rd_chunk_nand(struct yaffs_dev *dev,
+			       int nand_chunk,
+			       u8 * data,
+			       struct yaffs_spare *spare,
+			       enum yaffs_ecc_result *ecc_result,
+			       int correct_errors)
+{
+	int ret_val;
+	struct yaffs_spare local_spare;
+
+	if (!spare && data) {
+		/* If we don't have a real spare, then we use a local one. */
+		/* Need this for the calculation of the ecc */
+		spare = &local_spare;
+	}
+
+	if (!dev->param.use_nand_ecc) {
+		ret_val =
+		    dev->param.read_chunk_fn(dev, nand_chunk, data, spare);
+		if (data && correct_errors) {
+			/* Do ECC correction */
+			/* Todo handle any errors */
+			int ecc_result1, ecc_result2;
+			u8 calc_ecc[3];
+
+			yaffs_ecc_cacl(data, calc_ecc);
+			ecc_result1 =
+			    yaffs_ecc_correct(data, spare->ecc1, calc_ecc);
+			yaffs_ecc_cacl(&data[256], calc_ecc);
+			ecc_result2 =
+			    yaffs_ecc_correct(&data[256], spare->ecc2,
+					      calc_ecc);
+
+			if (ecc_result1 > 0) {
+				yaffs_trace(YAFFS_TRACE_ERROR,
+					"**>>yaffs ecc error fix performed on chunk %d:0",
+					nand_chunk);
+				dev->n_ecc_fixed++;
+			} else if (ecc_result1 < 0) {
+				yaffs_trace(YAFFS_TRACE_ERROR,
+					"**>>yaffs ecc error unfixed on chunk %d:0",
+					nand_chunk);
+				dev->n_ecc_unfixed++;
+			}
+
+			if (ecc_result2 > 0) {
+				yaffs_trace(YAFFS_TRACE_ERROR,
+					"**>>yaffs ecc error fix performed on chunk %d:1",
+					nand_chunk);
+				dev->n_ecc_fixed++;
+			} else if (ecc_result2 < 0) {
+				yaffs_trace(YAFFS_TRACE_ERROR,
+					"**>>yaffs ecc error unfixed on chunk %d:1",
+					nand_chunk);
+				dev->n_ecc_unfixed++;
+			}
+
+			if (ecc_result1 || ecc_result2) {
+				/* We had a data problem on this page */
+				yaffs_handle_rd_data_error(dev, nand_chunk);
+			}
+
+			if (ecc_result1 < 0 || ecc_result2 < 0)
+				*ecc_result = YAFFS_ECC_RESULT_UNFIXED;
+			else if (ecc_result1 > 0 || ecc_result2 > 0)
+				*ecc_result = YAFFS_ECC_RESULT_FIXED;
+			else
+				*ecc_result = YAFFS_ECC_RESULT_NO_ERROR;
+		}
+	} else {
+		/* Must allocate enough memory for spare+2*sizeof(int) */
+		/* for ecc results from device. */
+		struct yaffs_nand_spare nspare;
+
+		memset(&nspare, 0, sizeof(nspare));
+
+		ret_val = dev->param.read_chunk_fn(dev, nand_chunk, data,
+						   (struct yaffs_spare *)
+						   &nspare);
+		memcpy(spare, &nspare, sizeof(struct yaffs_spare));
+		if (data && correct_errors) {
+			if (nspare.eccres1 > 0) {
+				yaffs_trace(YAFFS_TRACE_ERROR,
+					"**>>mtd ecc error fix performed on chunk %d:0",
+					nand_chunk);
+			} else if (nspare.eccres1 < 0) {
+				yaffs_trace(YAFFS_TRACE_ERROR,
+					"**>>mtd ecc error unfixed on chunk %d:0",
+					nand_chunk);
+			}
+
+			if (nspare.eccres2 > 0) {
+				yaffs_trace(YAFFS_TRACE_ERROR,
+					"**>>mtd ecc error fix performed on chunk %d:1",
+					nand_chunk);
+			} else if (nspare.eccres2 < 0) {
+				yaffs_trace(YAFFS_TRACE_ERROR,
+					"**>>mtd ecc error unfixed on chunk %d:1",
+					nand_chunk);
+			}
+
+			if (nspare.eccres1 || nspare.eccres2) {
+				/* We had a data problem on this page */
+				yaffs_handle_rd_data_error(dev, nand_chunk);
+			}
+
+			if (nspare.eccres1 < 0 || nspare.eccres2 < 0)
+				*ecc_result = YAFFS_ECC_RESULT_UNFIXED;
+			else if (nspare.eccres1 > 0 || nspare.eccres2 > 0)
+				*ecc_result = YAFFS_ECC_RESULT_FIXED;
+			else
+				*ecc_result = YAFFS_ECC_RESULT_NO_ERROR;
+
+		}
+	}
+	return ret_val;
+}
+
+/*
+ * Functions for robustisizing
+ */
+
+static void yaffs_handle_rd_data_error(struct yaffs_dev *dev, int nand_chunk)
+{
+	int flash_block = nand_chunk / dev->param.chunks_per_block;
+
+	/* Mark the block for retirement */
+	yaffs_get_block_info(dev,
+			     flash_block + dev->block_offset)->needs_retiring =
+	    1;
+	yaffs_trace(YAFFS_TRACE_ERROR | YAFFS_TRACE_BAD_BLOCKS,
+		"**>>Block %d marked for retirement",
+		flash_block);
+
+	/* TODO:
+	 * Just do a garbage collection on the affected block
+	 * then retire the block
+	 * NB recursion
+	 */
+}
+
+int yaffs_tags_compat_wr(struct yaffs_dev *dev,
+			 int nand_chunk,
+			 const u8 * data, const struct yaffs_ext_tags *ext_tags)
+{
+	struct yaffs_spare spare;
+	struct yaffs_tags tags;
+
+	yaffs_spare_init(&spare);
+
+	if (ext_tags->is_deleted)
+		spare.page_status = 0;
+	else {
+		tags.obj_id = ext_tags->obj_id;
+		tags.chunk_id = ext_tags->chunk_id;
+
+		tags.n_bytes_lsb = ext_tags->n_bytes & 0x3ff;
+
+		if (dev->data_bytes_per_chunk >= 1024)
+			tags.n_bytes_msb = (ext_tags->n_bytes >> 10) & 3;
+		else
+			tags.n_bytes_msb = 3;
+
+		tags.serial_number = ext_tags->serial_number;
+
+		if (!dev->param.use_nand_ecc && data)
+			yaffs_calc_ecc(data, &spare);
+
+		yaffs_load_tags_to_spare(&spare, &tags);
+
+	}
+
+	return yaffs_wr_nand(dev, nand_chunk, data, &spare);
+}
+
+int yaffs_tags_compat_rd(struct yaffs_dev *dev,
+			 int nand_chunk,
+			 u8 * data, struct yaffs_ext_tags *ext_tags)
+{
+
+	struct yaffs_spare spare;
+	struct yaffs_tags tags;
+	enum yaffs_ecc_result ecc_result = YAFFS_ECC_RESULT_UNKNOWN;
+
+	static struct yaffs_spare spare_ff;
+	static int init;
+
+	if (!init) {
+		memset(&spare_ff, 0xFF, sizeof(spare_ff));
+		init = 1;
+	}
+
+	if (yaffs_rd_chunk_nand(dev, nand_chunk, data, &spare, &ecc_result, 1)) {
+		/* ext_tags may be NULL */
+		if (ext_tags) {
+
+			int deleted =
+			    (hweight8(spare.page_status) < 7) ? 1 : 0;
+
+			ext_tags->is_deleted = deleted;
+			ext_tags->ecc_result = ecc_result;
+			ext_tags->block_bad = 0;	/* We're reading it */
+			/* therefore it is not a bad block */
+			ext_tags->chunk_used =
+			    (memcmp(&spare_ff, &spare, sizeof(spare_ff)) !=
+			     0) ? 1 : 0;
+
+			if (ext_tags->chunk_used) {
+				yaffs_get_tags_from_spare(dev, &spare, &tags);
+
+				ext_tags->obj_id = tags.obj_id;
+				ext_tags->chunk_id = tags.chunk_id;
+				ext_tags->n_bytes = tags.n_bytes_lsb;
+
+				if (dev->data_bytes_per_chunk >= 1024)
+					ext_tags->n_bytes |=
+					    (((unsigned)tags.
+					      n_bytes_msb) << 10);
+
+				ext_tags->serial_number = tags.serial_number;
+			}
+		}
+
+		return YAFFS_OK;
+	} else {
+		return YAFFS_FAIL;
+	}
+}
+
+int yaffs_tags_compat_mark_bad(struct yaffs_dev *dev, int flash_block)
+{
+
+	struct yaffs_spare spare;
+
+	memset(&spare, 0xff, sizeof(struct yaffs_spare));
+
+	spare.block_status = 'Y';
+
+	yaffs_wr_nand(dev, flash_block * dev->param.chunks_per_block, NULL,
+		      &spare);
+	yaffs_wr_nand(dev, flash_block * dev->param.chunks_per_block + 1,
+		      NULL, &spare);
+
+	return YAFFS_OK;
+
+}
+
+int yaffs_tags_compat_query_block(struct yaffs_dev *dev,
+				  int block_no,
+				  enum yaffs_block_state *state,
+				  u32 * seq_number)
+{
+
+	struct yaffs_spare spare0, spare1;
+	static struct yaffs_spare spare_ff;
+	static int init;
+	enum yaffs_ecc_result dummy;
+
+	if (!init) {
+		memset(&spare_ff, 0xFF, sizeof(spare_ff));
+		init = 1;
+	}
+
+	*seq_number = 0;
+
+	yaffs_rd_chunk_nand(dev, block_no * dev->param.chunks_per_block, NULL,
+			    &spare0, &dummy, 1);
+	yaffs_rd_chunk_nand(dev, block_no * dev->param.chunks_per_block + 1,
+			    NULL, &spare1, &dummy, 1);
+
+	if (hweight8(spare0.block_status & spare1.block_status) < 7)
+		*state = YAFFS_BLOCK_STATE_DEAD;
+	else if (memcmp(&spare_ff, &spare0, sizeof(spare_ff)) == 0)
+		*state = YAFFS_BLOCK_STATE_EMPTY;
+	else
+		*state = YAFFS_BLOCK_STATE_NEEDS_SCANNING;
+
+	return YAFFS_OK;
+}
diff --git a/fs/yaffs2/yaffs_tagscompat.h b/fs/yaffs2/yaffs_tagscompat.h
new file mode 100644
index 0000000..8cd35dc
--- /dev/null
+++ b/fs/yaffs2/yaffs_tagscompat.h
@@ -0,0 +1,36 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+#ifndef __YAFFS_TAGSCOMPAT_H__
+#define __YAFFS_TAGSCOMPAT_H__
+
+#include "yaffs_guts.h"
+int yaffs_tags_compat_wr(struct yaffs_dev *dev,
+			 int nand_chunk,
+			 const u8 * data, const struct yaffs_ext_tags *tags);
+int yaffs_tags_compat_rd(struct yaffs_dev *dev,
+			 int nand_chunk,
+			 u8 * data, struct yaffs_ext_tags *tags);
+int yaffs_tags_compat_mark_bad(struct yaffs_dev *dev, int block_no);
+int yaffs_tags_compat_query_block(struct yaffs_dev *dev,
+				  int block_no,
+				  enum yaffs_block_state *state,
+				  u32 * seq_number);
+
+void yaffs_calc_tags_ecc(struct yaffs_tags *tags);
+int yaffs_check_tags_ecc(struct yaffs_tags *tags);
+int yaffs_count_bits(u8 byte);
+
+#endif
diff --git a/fs/yaffs2/yaffs_tagsvalidity.c b/fs/yaffs2/yaffs_tagsvalidity.c
new file mode 100644
index 0000000..4358d79
--- /dev/null
+++ b/fs/yaffs2/yaffs_tagsvalidity.c
@@ -0,0 +1,27 @@
+/*
+ * YAFFS: Yet Another Flash File System. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU General Public License version 2 as
+ * published by the Free Software Foundation.
+ */
+
+#include "yaffs_tagsvalidity.h"
+
+void yaffs_init_tags(struct yaffs_ext_tags *tags)
+{
+	memset(tags, 0, sizeof(struct yaffs_ext_tags));
+	tags->validity0 = 0xAAAAAAAA;
+	tags->validity1 = 0x55555555;
+}
+
+int yaffs_validate_tags(struct yaffs_ext_tags *tags)
+{
+	return (tags->validity0 == 0xAAAAAAAA && tags->validity1 == 0x55555555);
+
+}
diff --git a/fs/yaffs2/yaffs_tagsvalidity.h b/fs/yaffs2/yaffs_tagsvalidity.h
new file mode 100644
index 0000000..36a021f
--- /dev/null
+++ b/fs/yaffs2/yaffs_tagsvalidity.h
@@ -0,0 +1,23 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+#ifndef __YAFFS_TAGS_VALIDITY_H__
+#define __YAFFS_TAGS_VALIDITY_H__
+
+#include "yaffs_guts.h"
+
+void yaffs_init_tags(struct yaffs_ext_tags *tags);
+int yaffs_validate_tags(struct yaffs_ext_tags *tags);
+#endif
diff --git a/fs/yaffs2/yaffs_trace.h b/fs/yaffs2/yaffs_trace.h
new file mode 100644
index 0000000..6273dbf
--- /dev/null
+++ b/fs/yaffs2/yaffs_trace.h
@@ -0,0 +1,57 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+#ifndef __YTRACE_H__
+#define __YTRACE_H__
+
+extern unsigned int yaffs_trace_mask;
+extern unsigned int yaffs_wr_attempts;
+
+/*
+ * Tracing flags.
+ * The flags masked in YAFFS_TRACE_ALWAYS are always traced.
+ */
+
+#define YAFFS_TRACE_OS			0x00000002
+#define YAFFS_TRACE_ALLOCATE		0x00000004
+#define YAFFS_TRACE_SCAN		0x00000008
+#define YAFFS_TRACE_BAD_BLOCKS		0x00000010
+#define YAFFS_TRACE_ERASE		0x00000020
+#define YAFFS_TRACE_GC			0x00000040
+#define YAFFS_TRACE_WRITE		0x00000080
+#define YAFFS_TRACE_TRACING		0x00000100
+#define YAFFS_TRACE_DELETION		0x00000200
+#define YAFFS_TRACE_BUFFERS		0x00000400
+#define YAFFS_TRACE_NANDACCESS		0x00000800
+#define YAFFS_TRACE_GC_DETAIL		0x00001000
+#define YAFFS_TRACE_SCAN_DEBUG		0x00002000
+#define YAFFS_TRACE_MTD			0x00004000
+#define YAFFS_TRACE_CHECKPOINT		0x00008000
+
+#define YAFFS_TRACE_VERIFY		0x00010000
+#define YAFFS_TRACE_VERIFY_NAND		0x00020000
+#define YAFFS_TRACE_VERIFY_FULL		0x00040000
+#define YAFFS_TRACE_VERIFY_ALL		0x000F0000
+
+#define YAFFS_TRACE_SYNC		0x00100000
+#define YAFFS_TRACE_BACKGROUND		0x00200000
+#define YAFFS_TRACE_LOCK		0x00400000
+#define YAFFS_TRACE_MOUNT		0x00800000
+
+#define YAFFS_TRACE_ERROR		0x40000000
+#define YAFFS_TRACE_BUG			0x80000000
+#define YAFFS_TRACE_ALWAYS		0xF0000000
+
+#endif
diff --git a/fs/yaffs2/yaffs_verify.c b/fs/yaffs2/yaffs_verify.c
new file mode 100644
index 0000000..738c7f6
--- /dev/null
+++ b/fs/yaffs2/yaffs_verify.c
@@ -0,0 +1,535 @@
+/*
+ * YAFFS: Yet Another Flash File System. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU General Public License version 2 as
+ * published by the Free Software Foundation.
+ */
+
+#include "yaffs_verify.h"
+#include "yaffs_trace.h"
+#include "yaffs_bitmap.h"
+#include "yaffs_getblockinfo.h"
+#include "yaffs_nand.h"
+
+int yaffs_skip_verification(struct yaffs_dev *dev)
+{
+	dev = dev;
+	return !(yaffs_trace_mask &
+		 (YAFFS_TRACE_VERIFY | YAFFS_TRACE_VERIFY_FULL));
+}
+
+static int yaffs_skip_full_verification(struct yaffs_dev *dev)
+{
+	dev = dev;
+	return !(yaffs_trace_mask & (YAFFS_TRACE_VERIFY_FULL));
+}
+
+static int yaffs_skip_nand_verification(struct yaffs_dev *dev)
+{
+	dev = dev;
+	return !(yaffs_trace_mask & (YAFFS_TRACE_VERIFY_NAND));
+}
+
+static const char *block_state_name[] = {
+	"Unknown",
+	"Needs scanning",
+	"Scanning",
+	"Empty",
+	"Allocating",
+	"Full",
+	"Dirty",
+	"Checkpoint",
+	"Collecting",
+	"Dead"
+};
+
+void yaffs_verify_blk(struct yaffs_dev *dev, struct yaffs_block_info *bi, int n)
+{
+	int actually_used;
+	int in_use;
+
+	if (yaffs_skip_verification(dev))
+		return;
+
+	/* Report illegal runtime states */
+	if (bi->block_state >= YAFFS_NUMBER_OF_BLOCK_STATES)
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Block %d has undefined state %d",
+			n, bi->block_state);
+
+	switch (bi->block_state) {
+	case YAFFS_BLOCK_STATE_UNKNOWN:
+	case YAFFS_BLOCK_STATE_SCANNING:
+	case YAFFS_BLOCK_STATE_NEEDS_SCANNING:
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Block %d has bad run-state %s",
+			n, block_state_name[bi->block_state]);
+	}
+
+	/* Check pages in use and soft deletions are legal */
+
+	actually_used = bi->pages_in_use - bi->soft_del_pages;
+
+	if (bi->pages_in_use < 0
+	    || bi->pages_in_use > dev->param.chunks_per_block
+	    || bi->soft_del_pages < 0
+	    || bi->soft_del_pages > dev->param.chunks_per_block
+	    || actually_used < 0 || actually_used > dev->param.chunks_per_block)
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Block %d has illegal values pages_in_used %d soft_del_pages %d",
+			n, bi->pages_in_use, bi->soft_del_pages);
+
+	/* Check chunk bitmap legal */
+	in_use = yaffs_count_chunk_bits(dev, n);
+	if (in_use != bi->pages_in_use)
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Block %d has inconsistent values pages_in_use %d counted chunk bits %d",
+			n, bi->pages_in_use, in_use);
+
+}
+
+void yaffs_verify_collected_blk(struct yaffs_dev *dev,
+				struct yaffs_block_info *bi, int n)
+{
+	yaffs_verify_blk(dev, bi, n);
+
+	/* After collection the block should be in the erased state */
+
+	if (bi->block_state != YAFFS_BLOCK_STATE_COLLECTING &&
+	    bi->block_state != YAFFS_BLOCK_STATE_EMPTY) {
+		yaffs_trace(YAFFS_TRACE_ERROR,
+			"Block %d is in state %d after gc, should be erased",
+			n, bi->block_state);
+	}
+}
+
+void yaffs_verify_blocks(struct yaffs_dev *dev)
+{
+	int i;
+	int state_count[YAFFS_NUMBER_OF_BLOCK_STATES];
+	int illegal_states = 0;
+
+	if (yaffs_skip_verification(dev))
+		return;
+
+	memset(state_count, 0, sizeof(state_count));
+
+	for (i = dev->internal_start_block; i <= dev->internal_end_block; i++) {
+		struct yaffs_block_info *bi = yaffs_get_block_info(dev, i);
+		yaffs_verify_blk(dev, bi, i);
+
+		if (bi->block_state < YAFFS_NUMBER_OF_BLOCK_STATES)
+			state_count[bi->block_state]++;
+		else
+			illegal_states++;
+	}
+
+	yaffs_trace(YAFFS_TRACE_VERIFY,	"Block summary");
+
+	yaffs_trace(YAFFS_TRACE_VERIFY,
+		"%d blocks have illegal states",
+		illegal_states);
+	if (state_count[YAFFS_BLOCK_STATE_ALLOCATING] > 1)
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Too many allocating blocks");
+
+	for (i = 0; i < YAFFS_NUMBER_OF_BLOCK_STATES; i++)
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"%s %d blocks",
+			block_state_name[i], state_count[i]);
+
+	if (dev->blocks_in_checkpt != state_count[YAFFS_BLOCK_STATE_CHECKPOINT])
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Checkpoint block count wrong dev %d count %d",
+			dev->blocks_in_checkpt,
+			state_count[YAFFS_BLOCK_STATE_CHECKPOINT]);
+
+	if (dev->n_erased_blocks != state_count[YAFFS_BLOCK_STATE_EMPTY])
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Erased block count wrong dev %d count %d",
+			dev->n_erased_blocks,
+			state_count[YAFFS_BLOCK_STATE_EMPTY]);
+
+	if (state_count[YAFFS_BLOCK_STATE_COLLECTING] > 1)
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Too many collecting blocks %d (max is 1)",
+			state_count[YAFFS_BLOCK_STATE_COLLECTING]);
+}
+
+/*
+ * Verify the object header. oh must be valid, but obj and tags may be NULL in which
+ * case those tests will not be performed.
+ */
+void yaffs_verify_oh(struct yaffs_obj *obj, struct yaffs_obj_hdr *oh,
+		     struct yaffs_ext_tags *tags, int parent_check)
+{
+	if (obj && yaffs_skip_verification(obj->my_dev))
+		return;
+
+	if (!(tags && obj && oh)) {
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Verifying object header tags %p obj %p oh %p",
+			tags, obj, oh);
+		return;
+	}
+
+	if (oh->type <= YAFFS_OBJECT_TYPE_UNKNOWN ||
+	    oh->type > YAFFS_OBJECT_TYPE_MAX)
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Obj %d header type is illegal value 0x%x",
+			tags->obj_id, oh->type);
+
+	if (tags->obj_id != obj->obj_id)
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Obj %d header mismatch obj_id %d",
+			tags->obj_id, obj->obj_id);
+
+	/*
+	 * Check that the object's parent ids match if parent_check requested.
+	 *
+	 * Tests do not apply to the root object.
+	 */
+
+	if (parent_check && tags->obj_id > 1 && !obj->parent)
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Obj %d header mismatch parent_id %d obj->parent is NULL",
+			tags->obj_id, oh->parent_obj_id);
+
+	if (parent_check && obj->parent &&
+	    oh->parent_obj_id != obj->parent->obj_id &&
+	    (oh->parent_obj_id != YAFFS_OBJECTID_UNLINKED ||
+	     obj->parent->obj_id != YAFFS_OBJECTID_DELETED))
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Obj %d header mismatch parent_id %d parent_obj_id %d",
+			tags->obj_id, oh->parent_obj_id,
+			obj->parent->obj_id);
+
+	if (tags->obj_id > 1 && oh->name[0] == 0)	/* Null name */
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Obj %d header name is NULL",
+			obj->obj_id);
+
+	if (tags->obj_id > 1 && ((u8) (oh->name[0])) == 0xff)	/* Trashed name */
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Obj %d header name is 0xFF",
+			obj->obj_id);
+}
+
+void yaffs_verify_file(struct yaffs_obj *obj)
+{
+	int required_depth;
+	int actual_depth;
+	u32 last_chunk;
+	u32 x;
+	u32 i;
+	struct yaffs_dev *dev;
+	struct yaffs_ext_tags tags;
+	struct yaffs_tnode *tn;
+	u32 obj_id;
+
+	if (!obj)
+		return;
+
+	if (yaffs_skip_verification(obj->my_dev))
+		return;
+
+	dev = obj->my_dev;
+	obj_id = obj->obj_id;
+
+	/* Check file size is consistent with tnode depth */
+	last_chunk =
+	    obj->variant.file_variant.file_size / dev->data_bytes_per_chunk + 1;
+	x = last_chunk >> YAFFS_TNODES_LEVEL0_BITS;
+	required_depth = 0;
+	while (x > 0) {
+		x >>= YAFFS_TNODES_INTERNAL_BITS;
+		required_depth++;
+	}
+
+	actual_depth = obj->variant.file_variant.top_level;
+
+	/* Check that the chunks in the tnode tree are all correct.
+	 * We do this by scanning through the tnode tree and
+	 * checking the tags for every chunk match.
+	 */
+
+	if (yaffs_skip_nand_verification(dev))
+		return;
+
+	for (i = 1; i <= last_chunk; i++) {
+		tn = yaffs_find_tnode_0(dev, &obj->variant.file_variant, i);
+
+		if (tn) {
+			u32 the_chunk = yaffs_get_group_base(dev, tn, i);
+			if (the_chunk > 0) {
+				yaffs_rd_chunk_tags_nand(dev, the_chunk, NULL,
+							 &tags);
+				if (tags.obj_id != obj_id || tags.chunk_id != i)
+				yaffs_trace(YAFFS_TRACE_VERIFY,
+					"Object %d chunk_id %d NAND mismatch chunk %d tags (%d:%d)",
+					 obj_id, i, the_chunk,
+					 tags.obj_id, tags.chunk_id);
+			}
+		}
+	}
+}
+
+void yaffs_verify_link(struct yaffs_obj *obj)
+{
+	if (obj && yaffs_skip_verification(obj->my_dev))
+		return;
+
+	/* Verify sane equivalent object */
+}
+
+void yaffs_verify_symlink(struct yaffs_obj *obj)
+{
+	if (obj && yaffs_skip_verification(obj->my_dev))
+		return;
+
+	/* Verify symlink string */
+}
+
+void yaffs_verify_special(struct yaffs_obj *obj)
+{
+	if (obj && yaffs_skip_verification(obj->my_dev))
+		return;
+}
+
+void yaffs_verify_obj(struct yaffs_obj *obj)
+{
+	struct yaffs_dev *dev;
+
+	u32 chunk_min;
+	u32 chunk_max;
+
+	u32 chunk_id_ok;
+	u32 chunk_in_range;
+	u32 chunk_wrongly_deleted;
+	u32 chunk_valid;
+
+	if (!obj)
+		return;
+
+	if (obj->being_created)
+		return;
+
+	dev = obj->my_dev;
+
+	if (yaffs_skip_verification(dev))
+		return;
+
+	/* Check sane object header chunk */
+
+	chunk_min = dev->internal_start_block * dev->param.chunks_per_block;
+	chunk_max =
+	    (dev->internal_end_block + 1) * dev->param.chunks_per_block - 1;
+
+	chunk_in_range = (((unsigned)(obj->hdr_chunk)) >= chunk_min &&
+			  ((unsigned)(obj->hdr_chunk)) <= chunk_max);
+	chunk_id_ok = chunk_in_range || (obj->hdr_chunk == 0);
+	chunk_valid = chunk_in_range &&
+	    yaffs_check_chunk_bit(dev,
+				  obj->hdr_chunk / dev->param.chunks_per_block,
+				  obj->hdr_chunk % dev->param.chunks_per_block);
+	chunk_wrongly_deleted = chunk_in_range && !chunk_valid;
+
+	if (!obj->fake && (!chunk_id_ok || chunk_wrongly_deleted))
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Obj %d has chunk_id %d %s %s",
+			obj->obj_id, obj->hdr_chunk,
+			chunk_id_ok ? "" : ",out of range",
+			chunk_wrongly_deleted ? ",marked as deleted" : "");
+
+	if (chunk_valid && !yaffs_skip_nand_verification(dev)) {
+		struct yaffs_ext_tags tags;
+		struct yaffs_obj_hdr *oh;
+		u8 *buffer = yaffs_get_temp_buffer(dev, __LINE__);
+
+		oh = (struct yaffs_obj_hdr *)buffer;
+
+		yaffs_rd_chunk_tags_nand(dev, obj->hdr_chunk, buffer, &tags);
+
+		yaffs_verify_oh(obj, oh, &tags, 1);
+
+		yaffs_release_temp_buffer(dev, buffer, __LINE__);
+	}
+
+	/* Verify it has a parent */
+	if (obj && !obj->fake && (!obj->parent || obj->parent->my_dev != dev)) {
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Obj %d has parent pointer %p which does not look like an object",
+			obj->obj_id, obj->parent);
+	}
+
+	/* Verify parent is a directory */
+	if (obj->parent
+	    && obj->parent->variant_type != YAFFS_OBJECT_TYPE_DIRECTORY) {
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Obj %d's parent is not a directory (type %d)",
+			obj->obj_id, obj->parent->variant_type);
+	}
+
+	switch (obj->variant_type) {
+	case YAFFS_OBJECT_TYPE_FILE:
+		yaffs_verify_file(obj);
+		break;
+	case YAFFS_OBJECT_TYPE_SYMLINK:
+		yaffs_verify_symlink(obj);
+		break;
+	case YAFFS_OBJECT_TYPE_DIRECTORY:
+		yaffs_verify_dir(obj);
+		break;
+	case YAFFS_OBJECT_TYPE_HARDLINK:
+		yaffs_verify_link(obj);
+		break;
+	case YAFFS_OBJECT_TYPE_SPECIAL:
+		yaffs_verify_special(obj);
+		break;
+	case YAFFS_OBJECT_TYPE_UNKNOWN:
+	default:
+		yaffs_trace(YAFFS_TRACE_VERIFY,
+			"Obj %d has illegaltype %d",
+		   obj->obj_id, obj->variant_type);
+		break;
+	}
+}
+
+void yaffs_verify_objects(struct yaffs_dev *dev)
+{
+	struct yaffs_obj *obj;
+	int i;
+	struct list_head *lh;
+
+	if (yaffs_skip_verification(dev))
+		return;
+
+	/* Iterate through the objects in each hash entry */
+
+	for (i = 0; i < YAFFS_NOBJECT_BUCKETS; i++) {
+		list_for_each(lh, &dev->obj_bucket[i].list) {
+			if (lh) {
+				obj =
+				    list_entry(lh, struct yaffs_obj, hash_link);
+				yaffs_verify_obj(obj);
+			}
+		}
+	}
+}
+
+void yaffs_verify_obj_in_dir(struct yaffs_obj *obj)
+{
+	struct list_head *lh;
+	struct yaffs_obj *list_obj;
+
+	int count = 0;
+
+	if (!obj) {
+		yaffs_trace(YAFFS_TRACE_ALWAYS, "No object to verify");
+		YBUG();
+		return;
+	}
+
+	if (yaffs_skip_verification(obj->my_dev))
+		return;
+
+	if (!obj->parent) {
+		yaffs_trace(YAFFS_TRACE_ALWAYS, "Object does not have parent" );
+		YBUG();
+		return;
+	}
+
+	if (obj->parent->variant_type != YAFFS_OBJECT_TYPE_DIRECTORY) {
+		yaffs_trace(YAFFS_TRACE_ALWAYS, "Parent is not directory");
+		YBUG();
+	}
+
+	/* Iterate through the objects in each hash entry */
+
+	list_for_each(lh, &obj->parent->variant.dir_variant.children) {
+		if (lh) {
+			list_obj = list_entry(lh, struct yaffs_obj, siblings);
+			yaffs_verify_obj(list_obj);
+			if (obj == list_obj)
+				count++;
+		}
+	}
+
+	if (count != 1) {
+		yaffs_trace(YAFFS_TRACE_ALWAYS,
+			"Object in directory %d times",
+			count);
+		YBUG();
+	}
+}
+
+void yaffs_verify_dir(struct yaffs_obj *directory)
+{
+	struct list_head *lh;
+	struct yaffs_obj *list_obj;
+
+	if (!directory) {
+		YBUG();
+		return;
+	}
+
+	if (yaffs_skip_full_verification(directory->my_dev))
+		return;
+
+	if (directory->variant_type != YAFFS_OBJECT_TYPE_DIRECTORY) {
+		yaffs_trace(YAFFS_TRACE_ALWAYS,
+			"Directory has wrong type: %d",
+			directory->variant_type);
+		YBUG();
+	}
+
+	/* Iterate through the objects in each hash entry */
+
+	list_for_each(lh, &directory->variant.dir_variant.children) {
+		if (lh) {
+			list_obj = list_entry(lh, struct yaffs_obj, siblings);
+			if (list_obj->parent != directory) {
+				yaffs_trace(YAFFS_TRACE_ALWAYS,
+					"Object in directory list has wrong parent %p",
+					list_obj->parent);
+				YBUG();
+			}
+			yaffs_verify_obj_in_dir(list_obj);
+		}
+	}
+}
+
+static int yaffs_free_verification_failures;
+
+void yaffs_verify_free_chunks(struct yaffs_dev *dev)
+{
+	int counted;
+	int difference;
+
+	if (yaffs_skip_verification(dev))
+		return;
+
+	counted = yaffs_count_free_chunks(dev);
+
+	difference = dev->n_free_chunks - counted;
+
+	if (difference) {
+		yaffs_trace(YAFFS_TRACE_ALWAYS,
+		 	"Freechunks verification failure %d %d %d",
+			dev->n_free_chunks, counted, difference);
+		yaffs_free_verification_failures++;
+	}
+}
+
+int yaffs_verify_file_sane(struct yaffs_obj *in)
+{
+	in = in;
+	return YAFFS_OK;
+}
+
diff --git a/fs/yaffs2/yaffs_verify.h b/fs/yaffs2/yaffs_verify.h
new file mode 100644
index 0000000..cc6f889
--- /dev/null
+++ b/fs/yaffs2/yaffs_verify.h
@@ -0,0 +1,43 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+#ifndef __YAFFS_VERIFY_H__
+#define __YAFFS_VERIFY_H__
+
+#include "yaffs_guts.h"
+
+void yaffs_verify_blk(struct yaffs_dev *dev, struct yaffs_block_info *bi,
+		      int n);
+void yaffs_verify_collected_blk(struct yaffs_dev *dev,
+				struct yaffs_block_info *bi, int n);
+void yaffs_verify_blocks(struct yaffs_dev *dev);
+
+void yaffs_verify_oh(struct yaffs_obj *obj, struct yaffs_obj_hdr *oh,
+		     struct yaffs_ext_tags *tags, int parent_check);
+void yaffs_verify_file(struct yaffs_obj *obj);
+void yaffs_verify_link(struct yaffs_obj *obj);
+void yaffs_verify_symlink(struct yaffs_obj *obj);
+void yaffs_verify_special(struct yaffs_obj *obj);
+void yaffs_verify_obj(struct yaffs_obj *obj);
+void yaffs_verify_objects(struct yaffs_dev *dev);
+void yaffs_verify_obj_in_dir(struct yaffs_obj *obj);
+void yaffs_verify_dir(struct yaffs_obj *directory);
+void yaffs_verify_free_chunks(struct yaffs_dev *dev);
+
+int yaffs_verify_file_sane(struct yaffs_obj *obj);
+
+int yaffs_skip_verification(struct yaffs_dev *dev);
+
+#endif
diff --git a/fs/yaffs2/yaffs_vfs.c b/fs/yaffs2/yaffs_vfs.c
new file mode 100644
index 0000000..d5b8753
--- /dev/null
+++ b/fs/yaffs2/yaffs_vfs.c
@@ -0,0 +1,2792 @@
+/*
+ * YAFFS: Yet Another Flash File System. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ * Acknowledgements:
+ * Luc van OostenRyck for numerous patches.
+ * Nick Bane for numerous patches.
+ * Nick Bane for 2.5/2.6 integration.
+ * Andras Toth for mknod rdev issue.
+ * Michael Fischer for finding the problem with inode inconsistency.
+ * Some code bodily lifted from JFFS
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU General Public License version 2 as
+ * published by the Free Software Foundation.
+ */
+
+/*
+ *
+ * This is the file system front-end to YAFFS that hooks it up to
+ * the VFS.
+ *
+ * Special notes:
+ * >> 2.4: sb->u.generic_sbp points to the struct yaffs_dev associated with
+ *         this superblock
+ * >> 2.6: sb->s_fs_info  points to the struct yaffs_dev associated with this
+ *         superblock
+ * >> inode->u.generic_ip points to the associated struct yaffs_obj.
+ */
+
+/*
+ * NB There are two variants of Linux VFS glue code. This variant supports
+ * a single version and should not include any multi-version code.
+ */
+#include <linux/version.h>
+
+#include <linux/kernel.h>
+#include <linux/module.h>
+#include <linux/slab.h>
+#include <linux/init.h>
+#include <linux/fs.h>
+#include <linux/proc_fs.h>
+#include <linux/smp_lock.h>
+#include <linux/pagemap.h>
+#include <linux/mtd/mtd.h>
+#include <linux/interrupt.h>
+#include <linux/string.h>
+#include <linux/ctype.h>
+#include <linux/namei.h>
+#include <linux/exportfs.h>
+#include <linux/kthread.h>
+#include <linux/delay.h>
+#include <linux/freezer.h>
+
+#include <asm/div64.h>
+
+#include <linux/statfs.h>
+
+#define UnlockPage(p) unlock_page(p)
+#define Page_Uptodate(page)	test_bit(PG_uptodate, &(page)->flags)
+
+#define yaffs_devname(sb, buf)	bdevname(sb->s_bdev, buf)
+
+#define YPROC_ROOT  NULL
+
+#define Y_INIT_TIMER(a)	init_timer_on_stack(a)
+
+#define WRITE_SIZE_STR "writesize"
+#define WRITE_SIZE(mtd) ((mtd)->writesize)
+
+static uint32_t YCALCBLOCKS(uint64_t partition_size, uint32_t block_size)
+{
+	uint64_t result = partition_size;
+	do_div(result, block_size);
+	return (uint32_t) result;
+}
+
+#include <linux/uaccess.h>
+#include <linux/mtd/mtd.h>
+
+#include "yportenv.h"
+#include "yaffs_trace.h"
+#include "yaffs_guts.h"
+#include "yaffs_attribs.h"
+
+#include "yaffs_linux.h"
+
+#include "yaffs_mtdif.h"
+#include "yaffs_mtdif1.h"
+#include "yaffs_mtdif2.h"
+
+unsigned int yaffs_trace_mask = YAFFS_TRACE_BAD_BLOCKS | YAFFS_TRACE_ALWAYS;
+unsigned int yaffs_wr_attempts = YAFFS_WR_ATTEMPTS;
+unsigned int yaffs_auto_checkpoint = 1;
+unsigned int yaffs_gc_control = 1;
+unsigned int yaffs_bg_enable = 1;
+
+/* Module Parameters */
+module_param(yaffs_trace_mask, uint, 0644);
+module_param(yaffs_wr_attempts, uint, 0644);
+module_param(yaffs_auto_checkpoint, uint, 0644);
+module_param(yaffs_gc_control, uint, 0644);
+module_param(yaffs_bg_enable, uint, 0644);
+
+
+#define yaffs_inode_to_obj_lv(iptr) ((iptr)->i_private)
+#define yaffs_inode_to_obj(iptr) ((struct yaffs_obj *)(yaffs_inode_to_obj_lv(iptr)))
+#define yaffs_dentry_to_obj(dptr) yaffs_inode_to_obj((dptr)->d_inode)
+#define yaffs_super_to_dev(sb)	((struct yaffs_dev *)sb->s_fs_info)
+
+#define update_dir_time(dir) do {\
+			(dir)->i_ctime = (dir)->i_mtime = CURRENT_TIME; \
+		} while(0)
+
+
+static unsigned yaffs_gc_control_callback(struct yaffs_dev *dev)
+{
+	return yaffs_gc_control;
+}
+
+static void yaffs_gross_lock(struct yaffs_dev *dev)
+{
+	yaffs_trace(YAFFS_TRACE_LOCK, "yaffs locking %p", current);
+	mutex_lock(&(yaffs_dev_to_lc(dev)->gross_lock));
+	yaffs_trace(YAFFS_TRACE_LOCK, "yaffs locked %p", current);
+}
+
+static void yaffs_gross_unlock(struct yaffs_dev *dev)
+{
+	yaffs_trace(YAFFS_TRACE_LOCK, "yaffs unlocking %p", current);
+	mutex_unlock(&(yaffs_dev_to_lc(dev)->gross_lock));
+}
+
+static void yaffs_fill_inode_from_obj(struct inode *inode,
+				      struct yaffs_obj *obj);
+
+static struct inode *yaffs_iget(struct super_block *sb, unsigned long ino)
+{
+	struct inode *inode;
+	struct yaffs_obj *obj;
+	struct yaffs_dev *dev = yaffs_super_to_dev(sb);
+
+	yaffs_trace(YAFFS_TRACE_OS, "yaffs_iget for %lu", ino);
+
+	inode = iget_locked(sb, ino);
+	if (!inode)
+		return ERR_PTR(-ENOMEM);
+	if (!(inode->i_state & I_NEW))
+		return inode;
+
+	/* NB This is called as a side effect of other functions, but
+	 * we had to release the lock to prevent deadlocks, so
+	 * need to lock again.
+	 */
+
+	yaffs_gross_lock(dev);
+
+	obj = yaffs_find_by_number(dev, inode->i_ino);
+
+	yaffs_fill_inode_from_obj(inode, obj);
+
+	yaffs_gross_unlock(dev);
+
+	unlock_new_inode(inode);
+	return inode;
+}
+
+struct inode *yaffs_get_inode(struct super_block *sb, int mode, int dev,
+			      struct yaffs_obj *obj)
+{
+	struct inode *inode;
+
+	if (!sb) {
+		yaffs_trace(YAFFS_TRACE_OS,
+			"yaffs_get_inode for NULL super_block!!");
+		return NULL;
+
+	}
+
+	if (!obj) {
+		yaffs_trace(YAFFS_TRACE_OS,
+			"yaffs_get_inode for NULL object!!");
+		return NULL;
+
+	}
+
+	yaffs_trace(YAFFS_TRACE_OS,
+		"yaffs_get_inode for object %d",
+		obj->obj_id);
+
+	inode = yaffs_iget(sb, obj->obj_id);
+	if (IS_ERR(inode))
+		return NULL;
+
+	/* NB Side effect: iget calls back to yaffs_read_inode(). */
+	/* iget also increments the inode's i_count */
+	/* NB You can't be holding gross_lock or deadlock will happen! */
+
+	return inode;
+}
+
+static int yaffs_mknod(struct inode *dir, struct dentry *dentry, int mode,
+		       dev_t rdev)
+{
+	struct inode *inode;
+
+	struct yaffs_obj *obj = NULL;
+	struct yaffs_dev *dev;
+
+	struct yaffs_obj *parent = yaffs_inode_to_obj(dir);
+
+	int error = -ENOSPC;
+	uid_t uid = current->cred->fsuid;
+	gid_t gid =
+	    (dir->i_mode & S_ISGID) ? dir->i_gid : current->cred->fsgid;
+
+	if ((dir->i_mode & S_ISGID) && S_ISDIR(mode))
+		mode |= S_ISGID;
+
+	if (parent) {
+		yaffs_trace(YAFFS_TRACE_OS,
+			"yaffs_mknod: parent object %d type %d",
+			parent->obj_id, parent->variant_type);
+	} else {
+		yaffs_trace(YAFFS_TRACE_OS,
+			"yaffs_mknod: could not get parent object");
+		return -EPERM;
+	}
+
+	yaffs_trace(YAFFS_TRACE_OS,
+		"yaffs_mknod: making oject for %s, mode %x dev %x",
+		dentry->d_name.name, mode, rdev);
+
+	dev = parent->my_dev;
+
+	yaffs_gross_lock(dev);
+
+	switch (mode & S_IFMT) {
+	default:
+		/* Special (socket, fifo, device...) */
+		yaffs_trace(YAFFS_TRACE_OS, "yaffs_mknod: making special");
+		obj =
+		    yaffs_create_special(parent, dentry->d_name.name, mode, uid,
+					 gid, old_encode_dev(rdev));
+		break;
+	case S_IFREG:		/* file          */
+		yaffs_trace(YAFFS_TRACE_OS, "yaffs_mknod: making file");
+		obj = yaffs_create_file(parent, dentry->d_name.name, mode, uid,
+					gid);
+		break;
+	case S_IFDIR:		/* directory */
+		yaffs_trace(YAFFS_TRACE_OS, "yaffs_mknod: making directory");
+		obj = yaffs_create_dir(parent, dentry->d_name.name, mode,
+				       uid, gid);
+		break;
+	case S_IFLNK:		/* symlink */
+		yaffs_trace(YAFFS_TRACE_OS, "yaffs_mknod: making symlink");
+		obj = NULL;	/* Do we ever get here? */
+		break;
+	}
+
+	/* Can not call yaffs_get_inode() with gross lock held */
+	yaffs_gross_unlock(dev);
+
+	if (obj) {
+		inode = yaffs_get_inode(dir->i_sb, mode, rdev, obj);
+		d_instantiate(dentry, inode);
+		update_dir_time(dir);
+		yaffs_trace(YAFFS_TRACE_OS,
+			"yaffs_mknod created object %d count = %d",
+			obj->obj_id, atomic_read(&inode->i_count));
+		error = 0;
+		yaffs_fill_inode_from_obj(dir, parent);
+	} else {
+		yaffs_trace(YAFFS_TRACE_OS, "yaffs_mknod failed making object");
+		error = -ENOMEM;
+	}
+
+	return error;
+}
+
+static int yaffs_mkdir(struct inode *dir, struct dentry *dentry, int mode)
+{
+	return yaffs_mknod(dir, dentry, mode | S_IFDIR, 0);
+}
+
+static int yaffs_create(struct inode *dir, struct dentry *dentry, int mode,
+			struct nameidata *n)
+{
+	return yaffs_mknod(dir, dentry, mode | S_IFREG, 0);
+}
+
+static int yaffs_link(struct dentry *old_dentry, struct inode *dir,
+		      struct dentry *dentry)
+{
+	struct inode *inode = old_dentry->d_inode;
+	struct yaffs_obj *obj = NULL;
+	struct yaffs_obj *link = NULL;
+	struct yaffs_dev *dev;
+
+	yaffs_trace(YAFFS_TRACE_OS, "yaffs_link");
+
+	obj = yaffs_inode_to_obj(inode);
+	dev = obj->my_dev;
+
+	yaffs_gross_lock(dev);
+
+	if (!S_ISDIR(inode->i_mode))	/* Don't link directories */
+		link =
+		    yaffs_link_obj(yaffs_inode_to_obj(dir), dentry->d_name.name,
+				   obj);
+
+	if (link) {
+		old_dentry->d_inode->i_nlink = yaffs_get_obj_link_count(obj);
+		d_instantiate(dentry, old_dentry->d_inode);
+		atomic_inc(&old_dentry->d_inode->i_count);
+		yaffs_trace(YAFFS_TRACE_OS,
+			"yaffs_link link count %d i_count %d",
+			old_dentry->d_inode->i_nlink,
+			atomic_read(&old_dentry->d_inode->i_count));
+	}
+
+	yaffs_gross_unlock(dev);
+
+	if (link) {
+		update_dir_time(dir);
+		return 0;
+	}
+
+	return -EPERM;
+}
+
+static int yaffs_symlink(struct inode *dir, struct dentry *dentry,
+			 const char *symname)
+{
+	struct yaffs_obj *obj;
+	struct yaffs_dev *dev;
+	uid_t uid = current->cred->fsuid;
+	gid_t gid =
+	    (dir->i_mode & S_ISGID) ? dir->i_gid : current->cred->fsgid;
+
+	yaffs_trace(YAFFS_TRACE_OS, "yaffs_symlink");
+
+	dev = yaffs_inode_to_obj(dir)->my_dev;
+	yaffs_gross_lock(dev);
+	obj = yaffs_create_symlink(yaffs_inode_to_obj(dir), dentry->d_name.name,
+				   S_IFLNK | S_IRWXUGO, uid, gid, symname);
+	yaffs_gross_unlock(dev);
+
+	if (obj) {
+		struct inode *inode;
+
+		inode = yaffs_get_inode(dir->i_sb, obj->yst_mode, 0, obj);
+		d_instantiate(dentry, inode);
+		update_dir_time(dir);
+		yaffs_trace(YAFFS_TRACE_OS, "symlink created OK");
+		return 0;
+	} else {
+		yaffs_trace(YAFFS_TRACE_OS, "symlink not created");
+	}
+
+	return -ENOMEM;
+}
+
+static struct dentry *yaffs_lookup(struct inode *dir, struct dentry *dentry,
+				   struct nameidata *n)
+{
+	struct yaffs_obj *obj;
+	struct inode *inode = NULL;
+
+	struct yaffs_dev *dev = yaffs_inode_to_obj(dir)->my_dev;
+
+	if (current != yaffs_dev_to_lc(dev)->readdir_process)
+		yaffs_gross_lock(dev);
+
+	yaffs_trace(YAFFS_TRACE_OS,
+		"yaffs_lookup for %d:%s",
+		yaffs_inode_to_obj(dir)->obj_id, dentry->d_name.name);
+
+	obj = yaffs_find_by_name(yaffs_inode_to_obj(dir), dentry->d_name.name);
+
+	obj = yaffs_get_equivalent_obj(obj);	/* in case it was a hardlink */
+
+	/* Can't hold gross lock when calling yaffs_get_inode() */
+	if (current != yaffs_dev_to_lc(dev)->readdir_process)
+		yaffs_gross_unlock(dev);
+
+	if (obj) {
+		yaffs_trace(YAFFS_TRACE_OS,
+			"yaffs_lookup found %d", obj->obj_id);
+
+		inode = yaffs_get_inode(dir->i_sb, obj->yst_mode, 0, obj);
+
+		if (inode) {
+			yaffs_trace(YAFFS_TRACE_OS, "yaffs_loookup dentry");
+			d_add(dentry, inode);
+			/* return dentry; */
+			return NULL;
+		}
+
+	} else {
+		yaffs_trace(YAFFS_TRACE_OS, "yaffs_lookup not found");
+
+	}
+
+	d_add(dentry, inode);
+
+	return NULL;
+}
+
+static int yaffs_unlink(struct inode *dir, struct dentry *dentry)
+{
+	int ret_val;
+
+	struct yaffs_dev *dev;
+	struct yaffs_obj *obj;
+
+	yaffs_trace(YAFFS_TRACE_OS,
+		"yaffs_unlink %d:%s",
+		(int)(dir->i_ino), dentry->d_name.name);
+	obj = yaffs_inode_to_obj(dir);
+	dev = obj->my_dev;
+
+	yaffs_gross_lock(dev);
+
+	ret_val = yaffs_unlinker(obj, dentry->d_name.name);
+
+	if (ret_val == YAFFS_OK) {
+		dentry->d_inode->i_nlink--;
+		dir->i_version++;
+		yaffs_gross_unlock(dev);
+		mark_inode_dirty(dentry->d_inode);
+		update_dir_time(dir);
+		return 0;
+	}
+	yaffs_gross_unlock(dev);
+	return -ENOTEMPTY;
+}
+
+static int yaffs_sync_object(struct file *file, int datasync)
+{
+
+	struct yaffs_obj *obj;
+	struct yaffs_dev *dev;
+	struct dentry *dentry = file->f_path.dentry;
+
+	obj = yaffs_dentry_to_obj(dentry);
+
+	dev = obj->my_dev;
+
+	yaffs_trace(YAFFS_TRACE_OS | YAFFS_TRACE_SYNC, "yaffs_sync_object");
+	yaffs_gross_lock(dev);
+	yaffs_flush_file(obj, 1, datasync);
+	yaffs_gross_unlock(dev);
+	return 0;
+}
+/*
+ * The VFS layer already does all the dentry stuff for rename.
+ *
+ * NB: POSIX says you can rename an object over an old object of the same name
+ */
+static int yaffs_rename(struct inode *old_dir, struct dentry *old_dentry,
+			struct inode *new_dir, struct dentry *new_dentry)
+{
+	struct yaffs_dev *dev;
+	int ret_val = YAFFS_FAIL;
+	struct yaffs_obj *target;
+
+	yaffs_trace(YAFFS_TRACE_OS, "yaffs_rename");
+	dev = yaffs_inode_to_obj(old_dir)->my_dev;
+
+	yaffs_gross_lock(dev);
+
+	/* Check if the target is an existing directory that is not empty. */
+	target = yaffs_find_by_name(yaffs_inode_to_obj(new_dir),
+				    new_dentry->d_name.name);
+
+	if (target && target->variant_type == YAFFS_OBJECT_TYPE_DIRECTORY &&
+	    !list_empty(&target->variant.dir_variant.children)) {
+
+		yaffs_trace(YAFFS_TRACE_OS, "target is non-empty dir");
+
+		ret_val = YAFFS_FAIL;
+	} else {
+		/* Now does unlinking internally using shadowing mechanism */
+		yaffs_trace(YAFFS_TRACE_OS, "calling yaffs_rename_obj");
+
+		ret_val = yaffs_rename_obj(yaffs_inode_to_obj(old_dir),
+					   old_dentry->d_name.name,
+					   yaffs_inode_to_obj(new_dir),
+					   new_dentry->d_name.name);
+	}
+	yaffs_gross_unlock(dev);
+
+	if (ret_val == YAFFS_OK) {
+		if (target) {
+			new_dentry->d_inode->i_nlink--;
+			mark_inode_dirty(new_dentry->d_inode);
+		}
+
+		update_dir_time(old_dir);
+		if (old_dir != new_dir)
+			update_dir_time(new_dir);
+		return 0;
+	} else {
+		return -ENOTEMPTY;
+	}
+}
+
+static int yaffs_setattr(struct dentry *dentry, struct iattr *attr)
+{
+	struct inode *inode = dentry->d_inode;
+	int error = 0;
+	struct yaffs_dev *dev;
+
+	yaffs_trace(YAFFS_TRACE_OS,
+		"yaffs_setattr of object %d",
+		yaffs_inode_to_obj(inode)->obj_id);
+
+	/* Fail if a requested resize >= 2GB */
+	if (attr->ia_valid & ATTR_SIZE && (attr->ia_size >> 31))
+		error = -EINVAL;
+
+	if (error == 0)
+		error = inode_change_ok(inode, attr);
+	if (error == 0) {
+		int result;
+		if (!error) {
+			setattr_copy(inode, attr);
+			yaffs_trace(YAFFS_TRACE_OS, "inode_setattr called");
+			if (attr->ia_valid & ATTR_SIZE) {
+				truncate_setsize(inode, attr->ia_size);
+				inode->i_blocks = (inode->i_size + 511) >> 9;
+			}
+		}
+		dev = yaffs_inode_to_obj(inode)->my_dev;
+		if (attr->ia_valid & ATTR_SIZE) {
+			yaffs_trace(YAFFS_TRACE_OS, "resize to %d(%x)",
+					   (int)(attr->ia_size),
+					   (int)(attr->ia_size));
+		}
+		yaffs_gross_lock(dev);
+		result = yaffs_set_attribs(yaffs_inode_to_obj(inode), attr);
+		if (result == YAFFS_OK) {
+			error = 0;
+		} else {
+			error = -EPERM;
+		}
+		yaffs_gross_unlock(dev);
+
+	}
+
+	yaffs_trace(YAFFS_TRACE_OS, "yaffs_setattr done returning %d", error);
+
+	return error;
+}
+
+#ifdef CONFIG_YAFFS_XATTR
+static int yaffs_setxattr(struct dentry *dentry, const char *name,
+		   const void *value, size_t size, int flags)
+{
+	struct inode *inode = dentry->d_inode;
+	int error = 0;
+	struct yaffs_dev *dev;
+	struct yaffs_obj *obj = yaffs_inode_to_obj(inode);
+
+	yaffs_trace(YAFFS_TRACE_OS, "yaffs_setxattr of object %d", obj->obj_id);
+
+	if (error == 0) {
+		int result;
+		dev = obj->my_dev;
+		yaffs_gross_lock(dev);
+		result = yaffs_set_xattrib(obj, name, value, size, flags);
+		if (result == YAFFS_OK)
+			error = 0;
+		else if (result < 0)
+			error = result;
+		yaffs_gross_unlock(dev);
+
+	}
+	yaffs_trace(YAFFS_TRACE_OS, "yaffs_setxattr done returning %d", error);
+
+	return error;
+}
+
+static ssize_t yaffs_getxattr(struct dentry * dentry, const char *name, void *buff,
+		       size_t size)
+{
+	struct inode *inode = dentry->d_inode;
+	int error = 0;
+	struct yaffs_dev *dev;
+	struct yaffs_obj *obj = yaffs_inode_to_obj(inode);
+
+	yaffs_trace(YAFFS_TRACE_OS,
+		"yaffs_getxattr \"%s\" from object %d",
+		name, obj->obj_id);
+
+	if (error == 0) {
+		dev = obj->my_dev;
+		yaffs_gross_lock(dev);
+		error = yaffs_get_xattrib(obj, name, buff, size);
+		yaffs_gross_unlock(dev);
+
+	}
+	yaffs_trace(YAFFS_TRACE_OS, "yaffs_getxattr done returning %d", error);
+
+	return error;
+}
+
+static int yaffs_removexattr(struct dentry *dentry, const char *name)
+{
+	struct inode *inode = dentry->d_inode;
+	int error = 0;
+	struct yaffs_dev *dev;
+	struct yaffs_obj *obj = yaffs_inode_to_obj(inode);
+
+	yaffs_trace(YAFFS_TRACE_OS,
+		"yaffs_removexattr of object %d", obj->obj_id);
+
+	if (error == 0) {
+		int result;
+		dev = obj->my_dev;
+		yaffs_gross_lock(dev);
+		result = yaffs_remove_xattrib(obj, name);
+		if (result == YAFFS_OK)
+			error = 0;
+		else if (result < 0)
+			error = result;
+		yaffs_gross_unlock(dev);
+
+	}
+	yaffs_trace(YAFFS_TRACE_OS,
+		"yaffs_removexattr done returning %d", error);
+
+	return error;
+}
+
+static ssize_t yaffs_listxattr(struct dentry * dentry, char *buff, size_t size)
+{
+	struct inode *inode = dentry->d_inode;
+	int error = 0;
+	struct yaffs_dev *dev;
+	struct yaffs_obj *obj = yaffs_inode_to_obj(inode);
+
+	yaffs_trace(YAFFS_TRACE_OS,
+		"yaffs_listxattr of object %d", obj->obj_id);
+
+	if (error == 0) {
+		dev = obj->my_dev;
+		yaffs_gross_lock(dev);
+		error = yaffs_list_xattrib(obj, buff, size);
+		yaffs_gross_unlock(dev);
+
+	}
+	yaffs_trace(YAFFS_TRACE_OS,
+		"yaffs_listxattr done returning %d", error);
+
+	return error;
+}
+
+#endif
+
+static const struct inode_operations yaffs_dir_inode_operations = {
+	.create = yaffs_create,
+	.lookup = yaffs_lookup,
+	.link = yaffs_link,
+	.unlink = yaffs_unlink,
+	.symlink = yaffs_symlink,
+	.mkdir = yaffs_mkdir,
+	.rmdir = yaffs_unlink,
+	.mknod = yaffs_mknod,
+	.rename = yaffs_rename,
+	.setattr = yaffs_setattr,
+#ifdef CONFIG_YAFFS_XATTR
+	.setxattr = yaffs_setxattr,
+	.getxattr = yaffs_getxattr,
+	.listxattr = yaffs_listxattr,
+	.removexattr = yaffs_removexattr,
+#endif
+};
+/*-----------------------------------------------------------------*/
+/* Directory search context allows us to unlock access to yaffs during
+ * filldir without causing problems with the directory being modified.
+ * This is similar to the tried and tested mechanism used in yaffs direct.
+ *
+ * A search context iterates along a doubly linked list of siblings in the
+ * directory. If the iterating object is deleted then this would corrupt
+ * the list iteration, likely causing a crash. The search context avoids
+ * this by using the remove_obj_fn to move the search context to the
+ * next object before the object is deleted.
+ *
+ * Many readdirs (and thus seach conexts) may be alive simulateously so
+ * each struct yaffs_dev has a list of these.
+ *
+ * A seach context lives for the duration of a readdir.
+ *
+ * All these functions must be called while yaffs is locked.
+ */
+
+struct yaffs_search_context {
+	struct yaffs_dev *dev;
+	struct yaffs_obj *dir_obj;
+	struct yaffs_obj *next_return;
+	struct list_head others;
+};
+
+/*
+ * yaffs_new_search() creates a new search context, initialises it and
+ * adds it to the device's search context list.
+ *
+ * Called at start of readdir.
+ */
+static struct yaffs_search_context *yaffs_new_search(struct yaffs_obj *dir)
+{
+	struct yaffs_dev *dev = dir->my_dev;
+	struct yaffs_search_context *sc =
+	    kmalloc(sizeof(struct yaffs_search_context), GFP_NOFS);
+	if (sc) {
+		sc->dir_obj = dir;
+		sc->dev = dev;
+		if (list_empty(&sc->dir_obj->variant.dir_variant.children))
+			sc->next_return = NULL;
+		else
+			sc->next_return =
+			    list_entry(dir->variant.dir_variant.children.next,
+				       struct yaffs_obj, siblings);
+		INIT_LIST_HEAD(&sc->others);
+		list_add(&sc->others, &(yaffs_dev_to_lc(dev)->search_contexts));
+	}
+	return sc;
+}
+
+/*
+ * yaffs_search_end() disposes of a search context and cleans up.
+ */
+static void yaffs_search_end(struct yaffs_search_context *sc)
+{
+	if (sc) {
+		list_del(&sc->others);
+		kfree(sc);
+	}
+}
+
+/*
+ * yaffs_search_advance() moves a search context to the next object.
+ * Called when the search iterates or when an object removal causes
+ * the search context to be moved to the next object.
+ */
+static void yaffs_search_advance(struct yaffs_search_context *sc)
+{
+	if (!sc)
+		return;
+
+	if (sc->next_return == NULL ||
+	    list_empty(&sc->dir_obj->variant.dir_variant.children))
+		sc->next_return = NULL;
+	else {
+		struct list_head *next = sc->next_return->siblings.next;
+
+		if (next == &sc->dir_obj->variant.dir_variant.children)
+			sc->next_return = NULL;	/* end of list */
+		else
+			sc->next_return =
+			    list_entry(next, struct yaffs_obj, siblings);
+	}
+}
+
+/*
+ * yaffs_remove_obj_callback() is called when an object is unlinked.
+ * We check open search contexts and advance any which are currently
+ * on the object being iterated.
+ */
+static void yaffs_remove_obj_callback(struct yaffs_obj *obj)
+{
+
+	struct list_head *i;
+	struct yaffs_search_context *sc;
+	struct list_head *search_contexts =
+	    &(yaffs_dev_to_lc(obj->my_dev)->search_contexts);
+
+	/* Iterate through the directory search contexts.
+	 * If any are currently on the object being removed, then advance
+	 * the search context to the next object to prevent a hanging pointer.
+	 */
+	list_for_each(i, search_contexts) {
+		if (i) {
+			sc = list_entry(i, struct yaffs_search_context, others);
+			if (sc->next_return == obj)
+				yaffs_search_advance(sc);
+		}
+	}
+
+}
+
+static int yaffs_readdir(struct file *f, void *dirent, filldir_t filldir)
+{
+	struct yaffs_obj *obj;
+	struct yaffs_dev *dev;
+	struct yaffs_search_context *sc;
+	struct inode *inode = f->f_dentry->d_inode;
+	unsigned long offset, curoffs;
+	struct yaffs_obj *l;
+	int ret_val = 0;
+
+	char name[YAFFS_MAX_NAME_LENGTH + 1];
+
+	obj = yaffs_dentry_to_obj(f->f_dentry);
+	dev = obj->my_dev;
+
+	yaffs_gross_lock(dev);
+
+	yaffs_dev_to_lc(dev)->readdir_process = current;
+
+	offset = f->f_pos;
+
+	sc = yaffs_new_search(obj);
+	if (!sc) {
+		ret_val = -ENOMEM;
+		goto out;
+	}
+
+	yaffs_trace(YAFFS_TRACE_OS,
+		"yaffs_readdir: starting at %d", (int)offset);
+
+	if (offset == 0) {
+		yaffs_trace(YAFFS_TRACE_OS,
+			"yaffs_readdir: entry . ino %d",
+			(int)inode->i_ino);
+		yaffs_gross_unlock(dev);
+		if (filldir(dirent, ".", 1, offset, inode->i_ino, DT_DIR) < 0) {
+			yaffs_gross_lock(dev);
+			goto out;
+		}
+		yaffs_gross_lock(dev);
+		offset++;
+		f->f_pos++;
+	}
+	if (offset == 1) {
+		yaffs_trace(YAFFS_TRACE_OS,
+			"yaffs_readdir: entry .. ino %d",
+			(int)f->f_dentry->d_parent->d_inode->i_ino);
+		yaffs_gross_unlock(dev);
+		if (filldir(dirent, "..", 2, offset,
+			    f->f_dentry->d_parent->d_inode->i_ino,
+			    DT_DIR) < 0) {
+			yaffs_gross_lock(dev);
+			goto out;
+		}
+		yaffs_gross_lock(dev);
+		offset++;
+		f->f_pos++;
+	}
+
+	curoffs = 1;
+
+	/* If the directory has changed since the open or last call to
+	   readdir, rewind to after the 2 canned entries. */
+	if (f->f_version != inode->i_version) {
+		offset = 2;
+		f->f_pos = offset;
+		f->f_version = inode->i_version;
+	}
+
+	while (sc->next_return) {
+		curoffs++;
+		l = sc->next_return;
+		if (curoffs >= offset) {
+			int this_inode = yaffs_get_obj_inode(l);
+			int this_type = yaffs_get_obj_type(l);
+
+			yaffs_get_obj_name(l, name, YAFFS_MAX_NAME_LENGTH + 1);
+			yaffs_trace(YAFFS_TRACE_OS,
+				"yaffs_readdir: %s inode %d",
+				name, yaffs_get_obj_inode(l));
+
+			yaffs_gross_unlock(dev);
+
+			if (filldir(dirent,
+				    name,
+				    strlen(name),
+				    offset, this_inode, this_type) < 0) {
+				yaffs_gross_lock(dev);
+				goto out;
+			}
+
+			yaffs_gross_lock(dev);
+
+			offset++;
+			f->f_pos++;
+		}
+		yaffs_search_advance(sc);
+	}
+
+out:
+	yaffs_search_end(sc);
+	yaffs_dev_to_lc(dev)->readdir_process = NULL;
+	yaffs_gross_unlock(dev);
+
+	return ret_val;
+}
+
+static const struct file_operations yaffs_dir_operations = {
+	.read = generic_read_dir,
+	.readdir = yaffs_readdir,
+	.fsync = yaffs_sync_object,
+	.llseek = generic_file_llseek,
+};
+
+
+
+static int yaffs_file_flush(struct file *file, fl_owner_t id)
+{
+	struct yaffs_obj *obj = yaffs_dentry_to_obj(file->f_dentry);
+
+	struct yaffs_dev *dev = obj->my_dev;
+
+	yaffs_trace(YAFFS_TRACE_OS,
+	  	"yaffs_file_flush object %d (%s)",
+		obj->obj_id, obj->dirty ? "dirty" : "clean");
+
+	yaffs_gross_lock(dev);
+
+	yaffs_flush_file(obj, 1, 0);
+
+	yaffs_gross_unlock(dev);
+
+	return 0;
+}
+
+static const struct file_operations yaffs_file_operations = {
+	.read = do_sync_read,
+	.write = do_sync_write,
+	.aio_read = generic_file_aio_read,
+	.aio_write = generic_file_aio_write,
+	.mmap = generic_file_mmap,
+	.flush = yaffs_file_flush,
+	.fsync = yaffs_sync_object,
+	.splice_read = generic_file_splice_read,
+	.splice_write = generic_file_splice_write,
+	.llseek = generic_file_llseek,
+};
+
+
+/* ExportFS support */
+static struct inode *yaffs2_nfs_get_inode(struct super_block *sb, uint64_t ino,
+					  uint32_t generation)
+{
+	return yaffs_iget(sb, ino);
+}
+
+static struct dentry *yaffs2_fh_to_dentry(struct super_block *sb,
+					  struct fid *fid, int fh_len,
+					  int fh_type)
+{
+	return generic_fh_to_dentry(sb, fid, fh_len, fh_type,
+				    yaffs2_nfs_get_inode);
+}
+
+static struct dentry *yaffs2_fh_to_parent(struct super_block *sb,
+					  struct fid *fid, int fh_len,
+					  int fh_type)
+{
+	return generic_fh_to_parent(sb, fid, fh_len, fh_type,
+				    yaffs2_nfs_get_inode);
+}
+
+struct dentry *yaffs2_get_parent(struct dentry *dentry)
+{
+
+	struct super_block *sb = dentry->d_inode->i_sb;
+	struct dentry *parent = ERR_PTR(-ENOENT);
+	struct inode *inode;
+	unsigned long parent_ino;
+	struct yaffs_obj *d_obj;
+	struct yaffs_obj *parent_obj;
+
+	d_obj = yaffs_inode_to_obj(dentry->d_inode);
+
+	if (d_obj) {
+		parent_obj = d_obj->parent;
+		if (parent_obj) {
+			parent_ino = yaffs_get_obj_inode(parent_obj);
+			inode = yaffs_iget(sb, parent_ino);
+
+			if (IS_ERR(inode)) {
+				parent = ERR_CAST(inode);
+			} else {
+				parent = d_obtain_alias(inode);
+				if (!IS_ERR(parent)) {
+					parent = ERR_PTR(-ENOMEM);
+					iput(inode);
+				}
+			}
+		}
+	}
+
+	return parent;
+}
+
+/* Just declare a zero structure as a NULL value implies
+ * using the default functions of exportfs.
+ */
+
+static struct export_operations yaffs_export_ops = {
+	.fh_to_dentry = yaffs2_fh_to_dentry,
+	.fh_to_parent = yaffs2_fh_to_parent,
+	.get_parent = yaffs2_get_parent,
+};
+
+
+/*-----------------------------------------------------------------*/
+
+static int yaffs_readlink(struct dentry *dentry, char __user * buffer,
+			  int buflen)
+{
+	unsigned char *alias;
+	int ret;
+
+	struct yaffs_dev *dev = yaffs_dentry_to_obj(dentry)->my_dev;
+
+	yaffs_gross_lock(dev);
+
+	alias = yaffs_get_symlink_alias(yaffs_dentry_to_obj(dentry));
+
+	yaffs_gross_unlock(dev);
+
+	if (!alias)
+		return -ENOMEM;
+
+	ret = vfs_readlink(dentry, buffer, buflen, alias);
+	kfree(alias);
+	return ret;
+}
+
+static void *yaffs_follow_link(struct dentry *dentry, struct nameidata *nd)
+{
+	unsigned char *alias;
+	void *ret;
+	struct yaffs_dev *dev = yaffs_dentry_to_obj(dentry)->my_dev;
+
+	yaffs_gross_lock(dev);
+
+	alias = yaffs_get_symlink_alias(yaffs_dentry_to_obj(dentry));
+	yaffs_gross_unlock(dev);
+
+	if (!alias) {
+		ret = ERR_PTR(-ENOMEM);
+		goto out;
+	}
+
+	nd_set_link(nd, alias);
+	ret = (void *)alias;
+out:
+	return ret;
+}
+
+void yaffs_put_link(struct dentry *dentry, struct nameidata *nd, void *alias)
+{
+	kfree(alias);
+}
+
+
+static void yaffs_unstitch_obj(struct inode *inode, struct yaffs_obj *obj)
+{
+	/* Clear the association between the inode and
+	 * the struct yaffs_obj.
+	 */
+	obj->my_inode = NULL;
+	yaffs_inode_to_obj_lv(inode) = NULL;
+
+	/* If the object freeing was deferred, then the real
+	 * free happens now.
+	 * This should fix the inode inconsistency problem.
+	 */
+	yaffs_handle_defered_free(obj);
+}
+
+/* yaffs_evict_inode combines into one operation what was previously done in
+ * yaffs_clear_inode() and yaffs_delete_inode()
+ *
+ */
+static void yaffs_evict_inode(struct inode *inode)
+{
+	struct yaffs_obj *obj;
+	struct yaffs_dev *dev;
+	int deleteme = 0;
+
+	obj = yaffs_inode_to_obj(inode);
+
+	yaffs_trace(YAFFS_TRACE_OS,
+		"yaffs_evict_inode: ino %d, count %d %s",
+		(int)inode->i_ino,
+		atomic_read(&inode->i_count),
+		obj ? "object exists" : "null object");
+
+	if (!inode->i_nlink && !is_bad_inode(inode))
+		deleteme = 1;
+	truncate_inode_pages(&inode->i_data, 0);
+	end_writeback(inode);
+
+	if (deleteme && obj) {
+		dev = obj->my_dev;
+		yaffs_gross_lock(dev);
+		yaffs_del_obj(obj);
+		yaffs_gross_unlock(dev);
+	}
+	if (obj) {
+		dev = obj->my_dev;
+		yaffs_gross_lock(dev);
+		yaffs_unstitch_obj(inode, obj);
+		yaffs_gross_unlock(dev);
+	}
+
+}
+
+static void yaffs_touch_super(struct yaffs_dev *dev)
+{
+	struct super_block *sb = yaffs_dev_to_lc(dev)->super;
+
+	yaffs_trace(YAFFS_TRACE_OS, "yaffs_touch_super() sb = %p", sb);
+	if (sb)
+		sb->s_dirt = 1;
+}
+
+static int yaffs_readpage_nolock(struct file *f, struct page *pg)
+{
+	/* Lifted from jffs2 */
+
+	struct yaffs_obj *obj;
+	unsigned char *pg_buf;
+	int ret;
+
+	struct yaffs_dev *dev;
+
+	yaffs_trace(YAFFS_TRACE_OS,
+		"yaffs_readpage_nolock at %08x, size %08x",
+		(unsigned)(pg->index << PAGE_CACHE_SHIFT),
+		(unsigned)PAGE_CACHE_SIZE);
+
+	obj = yaffs_dentry_to_obj(f->f_dentry);
+
+	dev = obj->my_dev;
+
+	BUG_ON(!PageLocked(pg));
+
+	pg_buf = kmap(pg);
+	/* FIXME: Can kmap fail? */
+
+	yaffs_gross_lock(dev);
+
+	ret = yaffs_file_rd(obj, pg_buf,
+			    pg->index << PAGE_CACHE_SHIFT, PAGE_CACHE_SIZE);
+
+	yaffs_gross_unlock(dev);
+
+	if (ret >= 0)
+		ret = 0;
+
+	if (ret) {
+		ClearPageUptodate(pg);
+		SetPageError(pg);
+	} else {
+		SetPageUptodate(pg);
+		ClearPageError(pg);
+	}
+
+	flush_dcache_page(pg);
+	kunmap(pg);
+
+	yaffs_trace(YAFFS_TRACE_OS, "yaffs_readpage_nolock done");
+	return ret;
+}
+
+static int yaffs_readpage_unlock(struct file *f, struct page *pg)
+{
+	int ret = yaffs_readpage_nolock(f, pg);
+	UnlockPage(pg);
+	return ret;
+}
+
+static int yaffs_readpage(struct file *f, struct page *pg)
+{
+	int ret;
+
+	yaffs_trace(YAFFS_TRACE_OS, "yaffs_readpage");
+	ret = yaffs_readpage_unlock(f, pg);
+	yaffs_trace(YAFFS_TRACE_OS, "yaffs_readpage done");
+	return ret;
+}
+
+/* writepage inspired by/stolen from smbfs */
+
+static int yaffs_writepage(struct page *page, struct writeback_control *wbc)
+{
+	struct yaffs_dev *dev;
+	struct address_space *mapping = page->mapping;
+	struct inode *inode;
+	unsigned long end_index;
+	char *buffer;
+	struct yaffs_obj *obj;
+	int n_written = 0;
+	unsigned n_bytes;
+	loff_t i_size;
+
+	if (!mapping)
+		BUG();
+	inode = mapping->host;
+	if (!inode)
+		BUG();
+	i_size = i_size_read(inode);
+
+	end_index = i_size >> PAGE_CACHE_SHIFT;
+
+	if (page->index < end_index)
+		n_bytes = PAGE_CACHE_SIZE;
+	else {
+		n_bytes = i_size & (PAGE_CACHE_SIZE - 1);
+
+		if (page->index > end_index || !n_bytes) {
+			yaffs_trace(YAFFS_TRACE_OS,
+				"yaffs_writepage at %08x, inode size = %08x!!!",
+				(unsigned)(page->index << PAGE_CACHE_SHIFT),
+				(unsigned)inode->i_size);
+			yaffs_trace(YAFFS_TRACE_OS,
+			  "                -> don't care!!");
+
+			zero_user_segment(page, 0, PAGE_CACHE_SIZE);
+			set_page_writeback(page);
+			unlock_page(page);
+			end_page_writeback(page);
+			return 0;
+		}
+	}
+
+	if (n_bytes != PAGE_CACHE_SIZE)
+		zero_user_segment(page, n_bytes, PAGE_CACHE_SIZE);
+
+	get_page(page);
+
+	buffer = kmap(page);
+
+	obj = yaffs_inode_to_obj(inode);
+	dev = obj->my_dev;
+	yaffs_gross_lock(dev);
+
+	yaffs_trace(YAFFS_TRACE_OS,
+		"yaffs_writepage at %08x, size %08x",
+		(unsigned)(page->index << PAGE_CACHE_SHIFT), n_bytes);
+	yaffs_trace(YAFFS_TRACE_OS,
+		"writepag0: obj = %05x, ino = %05x",
+		(int)obj->variant.file_variant.file_size, (int)inode->i_size);
+
+	n_written = yaffs_wr_file(obj, buffer,
+				  page->index << PAGE_CACHE_SHIFT, n_bytes, 0);
+
+	yaffs_touch_super(dev);
+
+	yaffs_trace(YAFFS_TRACE_OS,
+		"writepag1: obj = %05x, ino = %05x",
+		(int)obj->variant.file_variant.file_size, (int)inode->i_size);
+
+	yaffs_gross_unlock(dev);
+
+	kunmap(page);
+	set_page_writeback(page);
+	unlock_page(page);
+	end_page_writeback(page);
+	put_page(page);
+
+	return (n_written == n_bytes) ? 0 : -ENOSPC;
+}
+
+/* Space holding and freeing is done to ensure we have space available for 
+ * write_begin/end.
+ * For now we just assume few parallel writes and check against a small
+ * number.
+ * Todo: need to do this with a counter to handle parallel reads better.
+ */
+
+static ssize_t yaffs_hold_space(struct file *f)
+{
+	struct yaffs_obj *obj;
+	struct yaffs_dev *dev;
+
+	int n_free_chunks;
+
+	obj = yaffs_dentry_to_obj(f->f_dentry);
+
+	dev = obj->my_dev;
+
+	yaffs_gross_lock(dev);
+
+	n_free_chunks = yaffs_get_n_free_chunks(dev);
+
+	yaffs_gross_unlock(dev);
+
+	return (n_free_chunks > 20) ? 1 : 0;
+}
+
+static void yaffs_release_space(struct file *f)
+{
+	struct yaffs_obj *obj;
+	struct yaffs_dev *dev;
+
+	obj = yaffs_dentry_to_obj(f->f_dentry);
+
+	dev = obj->my_dev;
+
+	yaffs_gross_lock(dev);
+
+	yaffs_gross_unlock(dev);
+}
+
+static int yaffs_write_begin(struct file *filp, struct address_space *mapping,
+			     loff_t pos, unsigned len, unsigned flags,
+			     struct page **pagep, void **fsdata)
+{
+	struct page *pg = NULL;
+	pgoff_t index = pos >> PAGE_CACHE_SHIFT;
+
+	int ret = 0;
+	int space_held = 0;
+
+	/* Get a page */
+	pg = grab_cache_page_write_begin(mapping, index, flags);
+
+	*pagep = pg;
+	if (!pg) {
+		ret = -ENOMEM;
+		goto out;
+	}
+	yaffs_trace(YAFFS_TRACE_OS,
+		"start yaffs_write_begin index %d(%x) uptodate %d",
+		(int)index, (int)index, Page_Uptodate(pg) ? 1 : 0);
+
+	/* Get fs space */
+	space_held = yaffs_hold_space(filp);
+
+	if (!space_held) {
+		ret = -ENOSPC;
+		goto out;
+	}
+
+	/* Update page if required */
+
+	if (!Page_Uptodate(pg))
+		ret = yaffs_readpage_nolock(filp, pg);
+
+	if (ret)
+		goto out;
+
+	/* Happy path return */
+	yaffs_trace(YAFFS_TRACE_OS, "end yaffs_write_begin - ok");
+
+	return 0;
+
+out:
+	yaffs_trace(YAFFS_TRACE_OS,
+		"end yaffs_write_begin fail returning %d", ret);
+	if (space_held)
+		yaffs_release_space(filp);
+	if (pg) {
+		unlock_page(pg);
+		page_cache_release(pg);
+	}
+	return ret;
+}
+
+static ssize_t yaffs_file_write(struct file *f, const char *buf, size_t n,
+				loff_t * pos)
+{
+	struct yaffs_obj *obj;
+	int n_written, ipos;
+	struct inode *inode;
+	struct yaffs_dev *dev;
+
+	obj = yaffs_dentry_to_obj(f->f_dentry);
+
+	dev = obj->my_dev;
+
+	yaffs_gross_lock(dev);
+
+	inode = f->f_dentry->d_inode;
+
+	if (!S_ISBLK(inode->i_mode) && f->f_flags & O_APPEND)
+		ipos = inode->i_size;
+	else
+		ipos = *pos;
+
+	if (!obj)
+		yaffs_trace(YAFFS_TRACE_OS,
+			"yaffs_file_write: hey obj is null!");
+	else
+		yaffs_trace(YAFFS_TRACE_OS,
+			"yaffs_file_write about to write writing %u(%x) bytes to object %d at %d(%x)",
+			(unsigned)n, (unsigned)n, obj->obj_id, ipos, ipos);
+
+	n_written = yaffs_wr_file(obj, buf, ipos, n, 0);
+
+	yaffs_touch_super(dev);
+
+	yaffs_trace(YAFFS_TRACE_OS,
+		"yaffs_file_write: %d(%x) bytes written",
+		(unsigned)n, (unsigned)n);
+
+	if (n_written > 0) {
+		ipos += n_written;
+		*pos = ipos;
+		if (ipos > inode->i_size) {
+			inode->i_size = ipos;
+			inode->i_blocks = (ipos + 511) >> 9;
+
+			yaffs_trace(YAFFS_TRACE_OS,
+				"yaffs_file_write size updated to %d bytes, %d blocks",
+				ipos, (int)(inode->i_blocks));
+		}
+
+	}
+	yaffs_gross_unlock(dev);
+	return (n_written == 0) && (n > 0) ? -ENOSPC : n_written;
+}
+
+static int yaffs_write_end(struct file *filp, struct address_space *mapping,
+			   loff_t pos, unsigned len, unsigned copied,
+			   struct page *pg, void *fsdadata)
+{
+	int ret = 0;
+	void *addr, *kva;
+	uint32_t offset_into_page = pos & (PAGE_CACHE_SIZE - 1);
+
+	kva = kmap(pg);
+	addr = kva + offset_into_page;
+
+	yaffs_trace(YAFFS_TRACE_OS,
+		"yaffs_write_end addr %p pos %x n_bytes %d",
+		addr, (unsigned)pos, copied);
+
+	ret = yaffs_file_write(filp, addr, copied, &pos);
+
+	if (ret != copied) {
+		yaffs_trace(YAFFS_TRACE_OS,
+			"yaffs_write_end not same size ret %d  copied %d",
+			ret, copied);
+		SetPageError(pg);
+	}
+
+	kunmap(pg);
+
+	yaffs_release_space(filp);
+	unlock_page(pg);
+	page_cache_release(pg);
+	return ret;
+}
+
+static int yaffs_statfs(struct dentry *dentry, struct kstatfs *buf)
+{
+	struct yaffs_dev *dev = yaffs_dentry_to_obj(dentry)->my_dev;
+	struct super_block *sb = dentry->d_sb;
+
+	yaffs_trace(YAFFS_TRACE_OS, "yaffs_statfs");
+
+	yaffs_gross_lock(dev);
+
+	buf->f_type = YAFFS_MAGIC;
+	buf->f_bsize = sb->s_blocksize;
+	buf->f_namelen = 255;
+
+	if (dev->data_bytes_per_chunk & (dev->data_bytes_per_chunk - 1)) {
+		/* Do this if chunk size is not a power of 2 */
+
+		uint64_t bytes_in_dev;
+		uint64_t bytes_free;
+
+		bytes_in_dev =
+		    ((uint64_t)
+		     ((dev->param.end_block - dev->param.start_block +
+		       1))) * ((uint64_t) (dev->param.chunks_per_block *
+					   dev->data_bytes_per_chunk));
+
+		do_div(bytes_in_dev, sb->s_blocksize);	/* bytes_in_dev becomes the number of blocks */
+		buf->f_blocks = bytes_in_dev;
+
+		bytes_free = ((uint64_t) (yaffs_get_n_free_chunks(dev))) *
+		    ((uint64_t) (dev->data_bytes_per_chunk));
+
+		do_div(bytes_free, sb->s_blocksize);
+
+		buf->f_bfree = bytes_free;
+
+	} else if (sb->s_blocksize > dev->data_bytes_per_chunk) {
+
+		buf->f_blocks =
+		    (dev->param.end_block - dev->param.start_block + 1) *
+		    dev->param.chunks_per_block /
+		    (sb->s_blocksize / dev->data_bytes_per_chunk);
+		buf->f_bfree =
+		    yaffs_get_n_free_chunks(dev) /
+		    (sb->s_blocksize / dev->data_bytes_per_chunk);
+	} else {
+		buf->f_blocks =
+		    (dev->param.end_block - dev->param.start_block + 1) *
+		    dev->param.chunks_per_block *
+		    (dev->data_bytes_per_chunk / sb->s_blocksize);
+
+		buf->f_bfree =
+		    yaffs_get_n_free_chunks(dev) *
+		    (dev->data_bytes_per_chunk / sb->s_blocksize);
+	}
+
+	buf->f_files = 0;
+	buf->f_ffree = 0;
+	buf->f_bavail = buf->f_bfree;
+
+	yaffs_gross_unlock(dev);
+	return 0;
+}
+
+static void yaffs_flush_inodes(struct super_block *sb)
+{
+	struct inode *iptr;
+	struct yaffs_obj *obj;
+
+	list_for_each_entry(iptr, &sb->s_inodes, i_sb_list) {
+		obj = yaffs_inode_to_obj(iptr);
+		if (obj) {
+			yaffs_trace(YAFFS_TRACE_OS,
+				"flushing obj %d", obj->obj_id);
+			yaffs_flush_file(obj, 1, 0);
+		}
+	}
+}
+
+static void yaffs_flush_super(struct super_block *sb, int do_checkpoint)
+{
+	struct yaffs_dev *dev = yaffs_super_to_dev(sb);
+	if (!dev)
+		return;
+
+	yaffs_flush_inodes(sb);
+	yaffs_update_dirty_dirs(dev);
+	yaffs_flush_whole_cache(dev);
+	if (do_checkpoint)
+		yaffs_checkpoint_save(dev);
+}
+
+static unsigned yaffs_bg_gc_urgency(struct yaffs_dev *dev)
+{
+	unsigned erased_chunks =
+	    dev->n_erased_blocks * dev->param.chunks_per_block;
+	struct yaffs_linux_context *context = yaffs_dev_to_lc(dev);
+	unsigned scattered = 0;	/* Free chunks not in an erased block */
+
+	if (erased_chunks < dev->n_free_chunks)
+		scattered = (dev->n_free_chunks - erased_chunks);
+
+	if (!context->bg_running)
+		return 0;
+	else if (scattered < (dev->param.chunks_per_block * 2))
+		return 0;
+	else if (erased_chunks > dev->n_free_chunks / 2)
+		return 0;
+	else if (erased_chunks > dev->n_free_chunks / 4)
+		return 1;
+	else
+		return 2;
+}
+
+static int yaffs_do_sync_fs(struct super_block *sb, int request_checkpoint)
+{
+
+	struct yaffs_dev *dev = yaffs_super_to_dev(sb);
+	unsigned int oneshot_checkpoint = (yaffs_auto_checkpoint & 4);
+	unsigned gc_urgent = yaffs_bg_gc_urgency(dev);
+	int do_checkpoint;
+
+	yaffs_trace(YAFFS_TRACE_OS | YAFFS_TRACE_SYNC | YAFFS_TRACE_BACKGROUND,
+		"yaffs_do_sync_fs: gc-urgency %d %s %s%s",
+		gc_urgent,
+		sb->s_dirt ? "dirty" : "clean",
+		request_checkpoint ? "checkpoint requested" : "no checkpoint",
+		oneshot_checkpoint ? " one-shot" : "");
+
+	yaffs_gross_lock(dev);
+	do_checkpoint = ((request_checkpoint && !gc_urgent) ||
+			 oneshot_checkpoint) && !dev->is_checkpointed;
+
+	if (sb->s_dirt || do_checkpoint) {
+		yaffs_flush_super(sb, !dev->is_checkpointed && do_checkpoint);
+		sb->s_dirt = 0;
+		if (oneshot_checkpoint)
+			yaffs_auto_checkpoint &= ~4;
+	}
+	yaffs_gross_unlock(dev);
+
+	return 0;
+}
+
+/*
+ * yaffs background thread functions .
+ * yaffs_bg_thread_fn() the thread function
+ * yaffs_bg_start() launches the background thread.
+ * yaffs_bg_stop() cleans up the background thread.
+ *
+ * NB: 
+ * The thread should only run after the yaffs is initialised
+ * The thread should be stopped before yaffs is unmounted.
+ * The thread should not do any writing while the fs is in read only.
+ */
+
+void yaffs_background_waker(unsigned long data)
+{
+	wake_up_process((struct task_struct *)data);
+}
+
+static int yaffs_bg_thread_fn(void *data)
+{
+	struct yaffs_dev *dev = (struct yaffs_dev *)data;
+	struct yaffs_linux_context *context = yaffs_dev_to_lc(dev);
+	unsigned long now = jiffies;
+	unsigned long next_dir_update = now;
+	unsigned long next_gc = now;
+	unsigned long expires;
+	unsigned int urgency;
+
+	int gc_result;
+	struct timer_list timer;
+
+	yaffs_trace(YAFFS_TRACE_BACKGROUND,
+		"yaffs_background starting for dev %p", (void *)dev);
+
+	set_freezable();
+	while (context->bg_running) {
+		yaffs_trace(YAFFS_TRACE_BACKGROUND, "yaffs_background");
+
+		if (kthread_should_stop())
+			break;
+
+		if (try_to_freeze())
+			continue;
+
+		yaffs_gross_lock(dev);
+
+		now = jiffies;
+
+		if (time_after(now, next_dir_update) && yaffs_bg_enable) {
+			yaffs_update_dirty_dirs(dev);
+			next_dir_update = now + HZ;
+		}
+
+		if (time_after(now, next_gc) && yaffs_bg_enable) {
+			if (!dev->is_checkpointed) {
+				urgency = yaffs_bg_gc_urgency(dev);
+				gc_result = yaffs_bg_gc(dev, urgency);
+				if (urgency > 1)
+					next_gc = now + HZ / 20 + 1;
+				else if (urgency > 0)
+					next_gc = now + HZ / 10 + 1;
+				else
+					next_gc = now + HZ * 2;
+			} else	{
+			        /*
+				 * gc not running so set to next_dir_update
+				 * to cut down on wake ups
+				 */
+				next_gc = next_dir_update;
+                        }
+		}
+		yaffs_gross_unlock(dev);
+		expires = next_dir_update;
+		if (time_before(next_gc, expires))
+			expires = next_gc;
+		if (time_before(expires, now))
+			expires = now + HZ;
+
+		Y_INIT_TIMER(&timer);
+		timer.expires = expires + 1;
+		timer.data = (unsigned long)current;
+		timer.function = yaffs_background_waker;
+
+		set_current_state(TASK_INTERRUPTIBLE);
+		add_timer(&timer);
+		schedule();
+		del_timer_sync(&timer);
+	}
+
+	return 0;
+}
+
+static int yaffs_bg_start(struct yaffs_dev *dev)
+{
+	int retval = 0;
+	struct yaffs_linux_context *context = yaffs_dev_to_lc(dev);
+
+	if (dev->read_only)
+		return -1;
+
+	context->bg_running = 1;
+
+	context->bg_thread = kthread_run(yaffs_bg_thread_fn,
+					 (void *)dev, "yaffs-bg-%d",
+					 context->mount_id);
+
+	if (IS_ERR(context->bg_thread)) {
+		retval = PTR_ERR(context->bg_thread);
+		context->bg_thread = NULL;
+		context->bg_running = 0;
+	}
+	return retval;
+}
+
+static void yaffs_bg_stop(struct yaffs_dev *dev)
+{
+	struct yaffs_linux_context *ctxt = yaffs_dev_to_lc(dev);
+
+	ctxt->bg_running = 0;
+
+	if (ctxt->bg_thread) {
+		kthread_stop(ctxt->bg_thread);
+		ctxt->bg_thread = NULL;
+	}
+}
+
+static void yaffs_write_super(struct super_block *sb)
+{
+	unsigned request_checkpoint = (yaffs_auto_checkpoint >= 2);
+
+	yaffs_trace(YAFFS_TRACE_OS | YAFFS_TRACE_SYNC | YAFFS_TRACE_BACKGROUND,
+		"yaffs_write_super%s",
+		request_checkpoint ? " checkpt" : "");
+
+	yaffs_do_sync_fs(sb, request_checkpoint);
+
+}
+
+static int yaffs_sync_fs(struct super_block *sb, int wait)
+{
+	unsigned request_checkpoint = (yaffs_auto_checkpoint >= 1);
+
+	yaffs_trace(YAFFS_TRACE_OS | YAFFS_TRACE_SYNC,
+		"yaffs_sync_fs%s", request_checkpoint ? " checkpt" : "");
+
+	yaffs_do_sync_fs(sb, request_checkpoint);
+
+	return 0;
+}
+
+
+static LIST_HEAD(yaffs_context_list);
+struct mutex yaffs_context_lock;
+
+
+
+struct yaffs_options {
+	int inband_tags;
+	int skip_checkpoint_read;
+	int skip_checkpoint_write;
+	int no_cache;
+	int tags_ecc_on;
+	int tags_ecc_overridden;
+	int lazy_loading_enabled;
+	int lazy_loading_overridden;
+	int empty_lost_and_found;
+	int empty_lost_and_found_overridden;
+};
+
+#define MAX_OPT_LEN 30
+static int yaffs_parse_options(struct yaffs_options *options,
+			       const char *options_str)
+{
+	char cur_opt[MAX_OPT_LEN + 1];
+	int p;
+	int error = 0;
+
+	/* Parse through the options which is a comma seperated list */
+
+	while (options_str && *options_str && !error) {
+		memset(cur_opt, 0, MAX_OPT_LEN + 1);
+		p = 0;
+
+		while (*options_str == ',')
+			options_str++;
+
+		while (*options_str && *options_str != ',') {
+			if (p < MAX_OPT_LEN) {
+				cur_opt[p] = *options_str;
+				p++;
+			}
+			options_str++;
+		}
+
+		if (!strcmp(cur_opt, "inband-tags")) {
+			options->inband_tags = 1;
+		} else if (!strcmp(cur_opt, "tags-ecc-off")) {
+			options->tags_ecc_on = 0;
+			options->tags_ecc_overridden = 1;
+		} else if (!strcmp(cur_opt, "tags-ecc-on")) {
+			options->tags_ecc_on = 1;
+			options->tags_ecc_overridden = 1;
+		} else if (!strcmp(cur_opt, "lazy-loading-off")) {
+			options->lazy_loading_enabled = 0;
+			options->lazy_loading_overridden = 1;
+		} else if (!strcmp(cur_opt, "lazy-loading-on")) {
+			options->lazy_loading_enabled = 1;
+			options->lazy_loading_overridden = 1;
+		} else if (!strcmp(cur_opt, "empty-lost-and-found-off")) {
+			options->empty_lost_and_found = 0;
+			options->empty_lost_and_found_overridden = 1;
+		} else if (!strcmp(cur_opt, "empty-lost-and-found-on")) {
+			options->empty_lost_and_found = 1;
+			options->empty_lost_and_found_overridden = 1;
+		} else if (!strcmp(cur_opt, "no-cache")) {
+			options->no_cache = 1;
+		} else if (!strcmp(cur_opt, "no-checkpoint-read")) {
+			options->skip_checkpoint_read = 1;
+		} else if (!strcmp(cur_opt, "no-checkpoint-write")) {
+			options->skip_checkpoint_write = 1;
+		} else if (!strcmp(cur_opt, "no-checkpoint")) {
+			options->skip_checkpoint_read = 1;
+			options->skip_checkpoint_write = 1;
+		} else {
+			printk(KERN_INFO "yaffs: Bad mount option \"%s\"\n",
+			       cur_opt);
+			error = 1;
+		}
+	}
+
+	return error;
+}
+
+static struct address_space_operations yaffs_file_address_operations = {
+	.readpage = yaffs_readpage,
+	.writepage = yaffs_writepage,
+	.write_begin = yaffs_write_begin,
+	.write_end = yaffs_write_end,
+};
+
+
+
+static const struct inode_operations yaffs_file_inode_operations = {
+	.setattr = yaffs_setattr,
+#ifdef CONFIG_YAFFS_XATTR
+	.setxattr = yaffs_setxattr,
+	.getxattr = yaffs_getxattr,
+	.listxattr = yaffs_listxattr,
+	.removexattr = yaffs_removexattr,
+#endif
+};
+
+static const struct inode_operations yaffs_symlink_inode_operations = {
+	.readlink = yaffs_readlink,
+	.follow_link = yaffs_follow_link,
+	.put_link = yaffs_put_link,
+	.setattr = yaffs_setattr,
+#ifdef CONFIG_YAFFS_XATTR
+	.setxattr = yaffs_setxattr,
+	.getxattr = yaffs_getxattr,
+	.listxattr = yaffs_listxattr,
+	.removexattr = yaffs_removexattr,
+#endif
+};
+
+static void yaffs_fill_inode_from_obj(struct inode *inode,
+				      struct yaffs_obj *obj)
+{
+	if (inode && obj) {
+
+		/* Check mode against the variant type and attempt to repair if broken. */
+		u32 mode = obj->yst_mode;
+		switch (obj->variant_type) {
+		case YAFFS_OBJECT_TYPE_FILE:
+			if (!S_ISREG(mode)) {
+				obj->yst_mode &= ~S_IFMT;
+				obj->yst_mode |= S_IFREG;
+			}
+
+			break;
+		case YAFFS_OBJECT_TYPE_SYMLINK:
+			if (!S_ISLNK(mode)) {
+				obj->yst_mode &= ~S_IFMT;
+				obj->yst_mode |= S_IFLNK;
+			}
+
+			break;
+		case YAFFS_OBJECT_TYPE_DIRECTORY:
+			if (!S_ISDIR(mode)) {
+				obj->yst_mode &= ~S_IFMT;
+				obj->yst_mode |= S_IFDIR;
+			}
+
+			break;
+		case YAFFS_OBJECT_TYPE_UNKNOWN:
+		case YAFFS_OBJECT_TYPE_HARDLINK:
+		case YAFFS_OBJECT_TYPE_SPECIAL:
+		default:
+			/* TODO? */
+			break;
+		}
+
+		inode->i_flags |= S_NOATIME;
+
+		inode->i_ino = obj->obj_id;
+		inode->i_mode = obj->yst_mode;
+		inode->i_uid = obj->yst_uid;
+		inode->i_gid = obj->yst_gid;
+
+		inode->i_rdev = old_decode_dev(obj->yst_rdev);
+
+		inode->i_atime.tv_sec = (time_t) (obj->yst_atime);
+		inode->i_atime.tv_nsec = 0;
+		inode->i_mtime.tv_sec = (time_t) obj->yst_mtime;
+		inode->i_mtime.tv_nsec = 0;
+		inode->i_ctime.tv_sec = (time_t) obj->yst_ctime;
+		inode->i_ctime.tv_nsec = 0;
+		inode->i_size = yaffs_get_obj_length(obj);
+		inode->i_blocks = (inode->i_size + 511) >> 9;
+
+		inode->i_nlink = yaffs_get_obj_link_count(obj);
+
+		yaffs_trace(YAFFS_TRACE_OS,
+			"yaffs_fill_inode mode %x uid %d gid %d size %d count %d",
+			inode->i_mode, inode->i_uid, inode->i_gid,
+			(int)inode->i_size, atomic_read(&inode->i_count));
+
+		switch (obj->yst_mode & S_IFMT) {
+		default:	/* fifo, device or socket */
+			init_special_inode(inode, obj->yst_mode,
+					   old_decode_dev(obj->yst_rdev));
+			break;
+		case S_IFREG:	/* file */
+			inode->i_op = &yaffs_file_inode_operations;
+			inode->i_fop = &yaffs_file_operations;
+			inode->i_mapping->a_ops =
+			    &yaffs_file_address_operations;
+			break;
+		case S_IFDIR:	/* directory */
+			inode->i_op = &yaffs_dir_inode_operations;
+			inode->i_fop = &yaffs_dir_operations;
+			break;
+		case S_IFLNK:	/* symlink */
+			inode->i_op = &yaffs_symlink_inode_operations;
+			break;
+		}
+
+		yaffs_inode_to_obj_lv(inode) = obj;
+
+		obj->my_inode = inode;
+
+	} else {
+		yaffs_trace(YAFFS_TRACE_OS,
+			"yaffs_fill_inode invalid parameters");
+	}
+}
+
+static void yaffs_put_super(struct super_block *sb)
+{
+	struct yaffs_dev *dev = yaffs_super_to_dev(sb);
+
+	yaffs_trace(YAFFS_TRACE_OS, "yaffs_put_super");
+
+	yaffs_trace(YAFFS_TRACE_OS | YAFFS_TRACE_BACKGROUND,
+		"Shutting down yaffs background thread");
+	yaffs_bg_stop(dev);
+	yaffs_trace(YAFFS_TRACE_OS | YAFFS_TRACE_BACKGROUND,
+		"yaffs background thread shut down");
+
+	yaffs_gross_lock(dev);
+
+	yaffs_flush_super(sb, 1);
+
+	if (yaffs_dev_to_lc(dev)->put_super_fn)
+		yaffs_dev_to_lc(dev)->put_super_fn(sb);
+
+	yaffs_deinitialise(dev);
+
+	yaffs_gross_unlock(dev);
+	mutex_lock(&yaffs_context_lock);
+	list_del_init(&(yaffs_dev_to_lc(dev)->context_list));
+	mutex_unlock(&yaffs_context_lock);
+
+	if (yaffs_dev_to_lc(dev)->spare_buffer) {
+		kfree(yaffs_dev_to_lc(dev)->spare_buffer);
+		yaffs_dev_to_lc(dev)->spare_buffer = NULL;
+	}
+
+	kfree(dev);
+}
+
+static void yaffs_mtd_put_super(struct super_block *sb)
+{
+	struct mtd_info *mtd = yaffs_dev_to_mtd(yaffs_super_to_dev(sb));
+
+	if (mtd->sync)
+		mtd->sync(mtd);
+
+	put_mtd_device(mtd);
+}
+
+static const struct super_operations yaffs_super_ops = {
+	.statfs = yaffs_statfs,
+	.put_super = yaffs_put_super,
+	.evict_inode = yaffs_evict_inode,
+	.sync_fs = yaffs_sync_fs,
+	.write_super = yaffs_write_super,
+};
+
+static struct super_block *yaffs_internal_read_super(int yaffs_version,
+						     struct super_block *sb,
+						     void *data, int silent)
+{
+	int n_blocks;
+	struct inode *inode = NULL;
+	struct dentry *root;
+	struct yaffs_dev *dev = 0;
+	char devname_buf[BDEVNAME_SIZE + 1];
+	struct mtd_info *mtd;
+	int err;
+	char *data_str = (char *)data;
+	struct yaffs_linux_context *context = NULL;
+	struct yaffs_param *param;
+
+	int read_only = 0;
+
+	struct yaffs_options options;
+
+	unsigned mount_id;
+	int found;
+	struct yaffs_linux_context *context_iterator;
+	struct list_head *l;
+
+	sb->s_magic = YAFFS_MAGIC;
+	sb->s_op = &yaffs_super_ops;
+	sb->s_flags |= MS_NOATIME;
+
+	read_only = ((sb->s_flags & MS_RDONLY) != 0);
+
+	sb->s_export_op = &yaffs_export_ops;
+
+	if (!sb)
+		printk(KERN_INFO "yaffs: sb is NULL\n");
+	else if (!sb->s_dev)
+		printk(KERN_INFO "yaffs: sb->s_dev is NULL\n");
+	else if (!yaffs_devname(sb, devname_buf))
+		printk(KERN_INFO "yaffs: devname is NULL\n");
+	else
+		printk(KERN_INFO "yaffs: dev is %d name is \"%s\" %s\n",
+		       sb->s_dev,
+		       yaffs_devname(sb, devname_buf), read_only ? "ro" : "rw");
+
+	if (!data_str)
+		data_str = "";
+
+	printk(KERN_INFO "yaffs: passed flags \"%s\"\n", data_str);
+
+	memset(&options, 0, sizeof(options));
+
+	if (yaffs_parse_options(&options, data_str)) {
+		/* Option parsing failed */
+		return NULL;
+	}
+
+	sb->s_blocksize = PAGE_CACHE_SIZE;
+	sb->s_blocksize_bits = PAGE_CACHE_SHIFT;
+
+	yaffs_trace(YAFFS_TRACE_OS,
+		"yaffs_read_super: Using yaffs%d", yaffs_version);
+	yaffs_trace(YAFFS_TRACE_OS,
+		"yaffs_read_super: block size %d", (int)(sb->s_blocksize));
+
+	yaffs_trace(YAFFS_TRACE_ALWAYS,
+		"Attempting MTD mount of %u.%u,\"%s\"",
+		MAJOR(sb->s_dev), MINOR(sb->s_dev),
+		yaffs_devname(sb, devname_buf));
+
+	/* Check it's an mtd device..... */
+	if (MAJOR(sb->s_dev) != MTD_BLOCK_MAJOR)
+		return NULL;	/* This isn't an mtd device */
+
+	/* Get the device */
+	mtd = get_mtd_device(NULL, MINOR(sb->s_dev));
+	if (!mtd) {
+		yaffs_trace(YAFFS_TRACE_ALWAYS,
+			"MTD device #%u doesn't appear to exist",
+			MINOR(sb->s_dev));
+		return NULL;
+	}
+	/* Check it's NAND */
+	if (mtd->type != MTD_NANDFLASH) {
+		yaffs_trace(YAFFS_TRACE_ALWAYS,
+			"MTD device is not NAND it's type %d",
+			mtd->type);
+		return NULL;
+	}
+
+	yaffs_trace(YAFFS_TRACE_OS, " erase %p", mtd->erase);
+	yaffs_trace(YAFFS_TRACE_OS, " read %p", mtd->read);
+	yaffs_trace(YAFFS_TRACE_OS, " write %p", mtd->write);
+	yaffs_trace(YAFFS_TRACE_OS, " readoob %p", mtd->read_oob);
+	yaffs_trace(YAFFS_TRACE_OS, " writeoob %p", mtd->write_oob);
+	yaffs_trace(YAFFS_TRACE_OS, " block_isbad %p", mtd->block_isbad);
+	yaffs_trace(YAFFS_TRACE_OS, " block_markbad %p", mtd->block_markbad);
+	yaffs_trace(YAFFS_TRACE_OS, " %s %d", WRITE_SIZE_STR, WRITE_SIZE(mtd));
+	yaffs_trace(YAFFS_TRACE_OS, " oobsize %d", mtd->oobsize);
+	yaffs_trace(YAFFS_TRACE_OS, " erasesize %d", mtd->erasesize);
+	yaffs_trace(YAFFS_TRACE_OS, " size %lld", mtd->size);
+
+#ifdef CONFIG_YAFFS_AUTO_YAFFS2
+
+	if (yaffs_version == 1 && WRITE_SIZE(mtd) >= 2048) {
+		yaffs_trace(YAFFS_TRACE_ALWAYS, "auto selecting yaffs2");
+		yaffs_version = 2;
+	}
+
+	/* Added NCB 26/5/2006 for completeness */
+	if (yaffs_version == 2 && !options.inband_tags
+	    && WRITE_SIZE(mtd) == 512) {
+		yaffs_trace(YAFFS_TRACE_ALWAYS, "auto selecting yaffs1");
+		yaffs_version = 1;
+	}
+#endif
+
+	if (yaffs_version == 2) {
+		/* Check for version 2 style functions */
+		if (!mtd->erase ||
+		    !mtd->block_isbad ||
+		    !mtd->block_markbad ||
+		    !mtd->read ||
+		    !mtd->write || !mtd->read_oob || !mtd->write_oob) {
+			yaffs_trace(YAFFS_TRACE_ALWAYS,
+				"MTD device does not support required functions");
+			return NULL;
+		}
+
+		if ((WRITE_SIZE(mtd) < YAFFS_MIN_YAFFS2_CHUNK_SIZE ||
+		     mtd->oobsize < YAFFS_MIN_YAFFS2_SPARE_SIZE) &&
+		    !options.inband_tags) {
+			yaffs_trace(YAFFS_TRACE_ALWAYS,
+				"MTD device does not have the right page sizes");
+			return NULL;
+		}
+	} else {
+		/* Check for V1 style functions */
+		if (!mtd->erase ||
+		    !mtd->read ||
+		    !mtd->write || !mtd->read_oob || !mtd->write_oob) {
+			yaffs_trace(YAFFS_TRACE_ALWAYS,
+				"MTD device does not support required functions");
+			return NULL;
+		}
+
+		if (WRITE_SIZE(mtd) < YAFFS_BYTES_PER_CHUNK ||
+		    mtd->oobsize != YAFFS_BYTES_PER_SPARE) {
+			yaffs_trace(YAFFS_TRACE_ALWAYS,
+				"MTD device does not support have the right page sizes");
+			return NULL;
+		}
+	}
+
+	/* OK, so if we got here, we have an MTD that's NAND and looks
+	 * like it has the right capabilities
+	 * Set the struct yaffs_dev up for mtd
+	 */
+
+	if (!read_only && !(mtd->flags & MTD_WRITEABLE)) {
+		read_only = 1;
+		printk(KERN_INFO
+		       "yaffs: mtd is read only, setting superblock read only");
+		sb->s_flags |= MS_RDONLY;
+	}
+
+	dev = kmalloc(sizeof(struct yaffs_dev), GFP_KERNEL);
+	context = kmalloc(sizeof(struct yaffs_linux_context), GFP_KERNEL);
+
+	if (!dev || !context) {
+		if (dev)
+			kfree(dev);
+		if (context)
+			kfree(context);
+		dev = NULL;
+		context = NULL;
+	}
+
+	if (!dev) {
+		/* Deep shit could not allocate device structure */
+		yaffs_trace(YAFFS_TRACE_ALWAYS,
+			"yaffs_read_super failed trying to allocate yaffs_dev");
+		return NULL;
+	}
+	memset(dev, 0, sizeof(struct yaffs_dev));
+	param = &(dev->param);
+
+	memset(context, 0, sizeof(struct yaffs_linux_context));
+	dev->os_context = context;
+	INIT_LIST_HEAD(&(context->context_list));
+	context->dev = dev;
+	context->super = sb;
+
+	dev->read_only = read_only;
+
+	sb->s_fs_info = dev;
+
+	dev->driver_context = mtd;
+	param->name = mtd->name;
+
+	/* Set up the memory size parameters.... */
+
+	n_blocks =
+	    YCALCBLOCKS(mtd->size,
+			(YAFFS_CHUNKS_PER_BLOCK * YAFFS_BYTES_PER_CHUNK));
+
+	param->start_block = 0;
+	param->end_block = n_blocks - 1;
+	param->chunks_per_block = YAFFS_CHUNKS_PER_BLOCK;
+	param->total_bytes_per_chunk = YAFFS_BYTES_PER_CHUNK;
+	param->n_reserved_blocks = 5;
+	param->n_caches = (options.no_cache) ? 0 : 10;
+	param->inband_tags = options.inband_tags;
+
+#ifdef CONFIG_YAFFS_DISABLE_LAZY_LOAD
+	param->disable_lazy_load = 1;
+#endif
+#ifdef CONFIG_YAFFS_XATTR
+	param->enable_xattr = 1;
+#endif
+	if (options.lazy_loading_overridden)
+		param->disable_lazy_load = !options.lazy_loading_enabled;
+
+#ifdef CONFIG_YAFFS_DISABLE_TAGS_ECC
+	param->no_tags_ecc = 1;
+#endif
+
+#ifdef CONFIG_YAFFS_DISABLE_BACKGROUND
+#else
+	param->defered_dir_update = 1;
+#endif
+
+	if (options.tags_ecc_overridden)
+		param->no_tags_ecc = !options.tags_ecc_on;
+
+#ifdef CONFIG_YAFFS_EMPTY_LOST_AND_FOUND
+	param->empty_lost_n_found = 1;
+#endif
+
+#ifdef CONFIG_YAFFS_DISABLE_BLOCK_REFRESHING
+	param->refresh_period = 0;
+#else
+	param->refresh_period = 500;
+#endif
+
+#ifdef CONFIG_YAFFS_ALWAYS_CHECK_CHUNK_ERASED
+	param->always_check_erased = 1;
+#endif
+
+	if (options.empty_lost_and_found_overridden)
+		param->empty_lost_n_found = options.empty_lost_and_found;
+
+	/* ... and the functions. */
+	if (yaffs_version == 2) {
+		param->write_chunk_tags_fn = nandmtd2_write_chunk_tags;
+		param->read_chunk_tags_fn = nandmtd2_read_chunk_tags;
+		param->bad_block_fn = nandmtd2_mark_block_bad;
+		param->query_block_fn = nandmtd2_query_block;
+		yaffs_dev_to_lc(dev)->spare_buffer = 
+		                kmalloc(mtd->oobsize, GFP_NOFS);
+		param->is_yaffs2 = 1;
+		param->total_bytes_per_chunk = mtd->writesize;
+		param->chunks_per_block = mtd->erasesize / mtd->writesize;
+		n_blocks = YCALCBLOCKS(mtd->size, mtd->erasesize);
+
+		param->start_block = 0;
+		param->end_block = n_blocks - 1;
+	} else {
+		/* use the MTD interface in yaffs_mtdif1.c */
+		param->write_chunk_tags_fn = nandmtd1_write_chunk_tags;
+		param->read_chunk_tags_fn = nandmtd1_read_chunk_tags;
+		param->bad_block_fn = nandmtd1_mark_block_bad;
+		param->query_block_fn = nandmtd1_query_block;
+		param->is_yaffs2 = 0;
+	}
+	/* ... and common functions */
+	param->erase_fn = nandmtd_erase_block;
+	param->initialise_flash_fn = nandmtd_initialise;
+
+	yaffs_dev_to_lc(dev)->put_super_fn = yaffs_mtd_put_super;
+
+	param->sb_dirty_fn = yaffs_touch_super;
+	param->gc_control = yaffs_gc_control_callback;
+
+	yaffs_dev_to_lc(dev)->super = sb;
+
+#ifndef CONFIG_YAFFS_DOES_ECC
+	param->use_nand_ecc = 1;
+#endif
+
+	param->skip_checkpt_rd = options.skip_checkpoint_read;
+	param->skip_checkpt_wr = options.skip_checkpoint_write;
+
+	mutex_lock(&yaffs_context_lock);
+	/* Get a mount id */
+	found = 0;
+	for (mount_id = 0; !found; mount_id++) {
+		found = 1;
+		list_for_each(l, &yaffs_context_list) {
+			context_iterator =
+			    list_entry(l, struct yaffs_linux_context,
+				       context_list);
+			if (context_iterator->mount_id == mount_id)
+				found = 0;
+		}
+	}
+	context->mount_id = mount_id;
+
+	list_add_tail(&(yaffs_dev_to_lc(dev)->context_list),
+		      &yaffs_context_list);
+	mutex_unlock(&yaffs_context_lock);
+
+	/* Directory search handling... */
+	INIT_LIST_HEAD(&(yaffs_dev_to_lc(dev)->search_contexts));
+	param->remove_obj_fn = yaffs_remove_obj_callback;
+
+	mutex_init(&(yaffs_dev_to_lc(dev)->gross_lock));
+
+	yaffs_gross_lock(dev);
+
+	err = yaffs_guts_initialise(dev);
+
+	yaffs_trace(YAFFS_TRACE_OS,
+		"yaffs_read_super: guts initialised %s",
+		(err == YAFFS_OK) ? "OK" : "FAILED");
+
+	if (err == YAFFS_OK)
+		yaffs_bg_start(dev);
+
+	if (!context->bg_thread)
+		param->defered_dir_update = 0;
+
+	/* Release lock before yaffs_get_inode() */
+	yaffs_gross_unlock(dev);
+
+	/* Create root inode */
+	if (err == YAFFS_OK)
+		inode = yaffs_get_inode(sb, S_IFDIR | 0755, 0, yaffs_root(dev));
+
+	if (!inode)
+		return NULL;
+
+	inode->i_op = &yaffs_dir_inode_operations;
+	inode->i_fop = &yaffs_dir_operations;
+
+	yaffs_trace(YAFFS_TRACE_OS, "yaffs_read_super: got root inode");
+
+	root = d_alloc_root(inode);
+
+	yaffs_trace(YAFFS_TRACE_OS, "yaffs_read_super: d_alloc_root done");
+
+	if (!root) {
+		iput(inode);
+		return NULL;
+	}
+	sb->s_root = root;
+	sb->s_dirt = !dev->is_checkpointed;
+	yaffs_trace(YAFFS_TRACE_ALWAYS,
+		"yaffs_read_super: is_checkpointed %d",
+		dev->is_checkpointed);
+
+	yaffs_trace(YAFFS_TRACE_OS, "yaffs_read_super: done");
+	return sb;
+}
+
+static int yaffs_internal_read_super_mtd(struct super_block *sb, void *data,
+					 int silent)
+{
+	return yaffs_internal_read_super(1, sb, data, silent) ? 0 : -EINVAL;
+}
+
+static int yaffs_read_super(struct file_system_type *fs,
+			    int flags, const char *dev_name,
+			    void *data, struct vfsmount *mnt)
+{
+
+	return get_sb_bdev(fs, flags, dev_name, data,
+			   yaffs_internal_read_super_mtd, mnt);
+}
+
+static struct file_system_type yaffs_fs_type = {
+	.owner = THIS_MODULE,
+	.name = "yaffs",
+	.get_sb = yaffs_read_super,
+	.kill_sb = kill_block_super,
+	.fs_flags = FS_REQUIRES_DEV,
+};
+
+#ifdef CONFIG_YAFFS_YAFFS2
+
+static int yaffs2_internal_read_super_mtd(struct super_block *sb, void *data,
+					  int silent)
+{
+	return yaffs_internal_read_super(2, sb, data, silent) ? 0 : -EINVAL;
+}
+
+static int yaffs2_read_super(struct file_system_type *fs,
+			     int flags, const char *dev_name, void *data,
+			     struct vfsmount *mnt)
+{
+	return get_sb_bdev(fs, flags, dev_name, data,
+			   yaffs2_internal_read_super_mtd, mnt);
+}
+
+static struct file_system_type yaffs2_fs_type = {
+	.owner = THIS_MODULE,
+	.name = "yaffs2",
+	.get_sb = yaffs2_read_super,
+	.kill_sb = kill_block_super,
+	.fs_flags = FS_REQUIRES_DEV,
+};
+#endif /* CONFIG_YAFFS_YAFFS2 */
+
+static struct proc_dir_entry *my_proc_entry;
+
+static char *yaffs_dump_dev_part0(char *buf, struct yaffs_dev *dev)
+{
+	struct yaffs_param *param = &dev->param;
+	buf += sprintf(buf, "start_block........... %d\n", param->start_block);
+	buf += sprintf(buf, "end_block............. %d\n", param->end_block);
+	buf += sprintf(buf, "total_bytes_per_chunk. %d\n",
+			param->total_bytes_per_chunk);
+	buf += sprintf(buf, "use_nand_ecc.......... %d\n",
+			param->use_nand_ecc);
+	buf += sprintf(buf, "no_tags_ecc........... %d\n", param->no_tags_ecc);
+	buf += sprintf(buf, "is_yaffs2............. %d\n", param->is_yaffs2);
+	buf += sprintf(buf, "inband_tags........... %d\n", param->inband_tags);
+	buf += sprintf(buf, "empty_lost_n_found.... %d\n",
+			param->empty_lost_n_found);
+	buf += sprintf(buf, "disable_lazy_load..... %d\n",
+			param->disable_lazy_load);
+	buf += sprintf(buf, "refresh_period........ %d\n",
+			param->refresh_period);
+	buf += sprintf(buf, "n_caches.............. %d\n", param->n_caches);
+	buf += sprintf(buf, "n_reserved_blocks..... %d\n",
+			param->n_reserved_blocks);
+	buf += sprintf(buf, "always_check_erased... %d\n",
+			param->always_check_erased);
+
+	return buf;
+}
+
+static char *yaffs_dump_dev_part1(char *buf, struct yaffs_dev *dev)
+{
+	buf +=
+	    sprintf(buf, "data_bytes_per_chunk.. %d\n",
+		    dev->data_bytes_per_chunk);
+	buf += sprintf(buf, "chunk_grp_bits........ %d\n", dev->chunk_grp_bits);
+	buf += sprintf(buf, "chunk_grp_size........ %d\n", dev->chunk_grp_size);
+	buf +=
+	    sprintf(buf, "n_erased_blocks....... %d\n", dev->n_erased_blocks);
+	buf +=
+	    sprintf(buf, "blocks_in_checkpt..... %d\n", dev->blocks_in_checkpt);
+	buf += sprintf(buf, "\n");
+	buf += sprintf(buf, "n_tnodes.............. %d\n", dev->n_tnodes);
+	buf += sprintf(buf, "n_obj................. %d\n", dev->n_obj);
+	buf += sprintf(buf, "n_free_chunks......... %d\n", dev->n_free_chunks);
+	buf += sprintf(buf, "\n");
+	buf += sprintf(buf, "n_page_writes......... %u\n", dev->n_page_writes);
+	buf += sprintf(buf, "n_page_reads.......... %u\n", dev->n_page_reads);
+	buf += sprintf(buf, "n_erasures............ %u\n", dev->n_erasures);
+	buf += sprintf(buf, "n_gc_copies........... %u\n", dev->n_gc_copies);
+	buf += sprintf(buf, "all_gcs............... %u\n", dev->all_gcs);
+	buf +=
+	    sprintf(buf, "passive_gc_count...... %u\n", dev->passive_gc_count);
+	buf +=
+	    sprintf(buf, "oldest_dirty_gc_count. %u\n",
+		    dev->oldest_dirty_gc_count);
+	buf += sprintf(buf, "n_gc_blocks........... %u\n", dev->n_gc_blocks);
+	buf += sprintf(buf, "bg_gcs................ %u\n", dev->bg_gcs);
+	buf +=
+	    sprintf(buf, "n_retired_writes...... %u\n", dev->n_retired_writes);
+	buf +=
+	    sprintf(buf, "n_retired_blocks...... %u\n", dev->n_retired_blocks);
+	buf += sprintf(buf, "n_ecc_fixed........... %u\n", dev->n_ecc_fixed);
+	buf += sprintf(buf, "n_ecc_unfixed......... %u\n", dev->n_ecc_unfixed);
+	buf +=
+	    sprintf(buf, "n_tags_ecc_fixed...... %u\n", dev->n_tags_ecc_fixed);
+	buf +=
+	    sprintf(buf, "n_tags_ecc_unfixed.... %u\n",
+		    dev->n_tags_ecc_unfixed);
+	buf += sprintf(buf, "cache_hits............ %u\n", dev->cache_hits);
+	buf +=
+	    sprintf(buf, "n_deleted_files....... %u\n", dev->n_deleted_files);
+	buf +=
+	    sprintf(buf, "n_unlinked_files...... %u\n", dev->n_unlinked_files);
+	buf += sprintf(buf, "refresh_count......... %u\n", dev->refresh_count);
+	buf += sprintf(buf, "n_bg_deletions........ %u\n", dev->n_bg_deletions);
+
+	return buf;
+}
+
+static int yaffs_proc_read(char *page,
+			   char **start,
+			   off_t offset, int count, int *eof, void *data)
+{
+	struct list_head *item;
+	char *buf = page;
+	int step = offset;
+	int n = 0;
+
+	/* Get proc_file_read() to step 'offset' by one on each sucessive call.
+	 * We use 'offset' (*ppos) to indicate where we are in dev_list.
+	 * This also assumes the user has posted a read buffer large
+	 * enough to hold the complete output; but that's life in /proc.
+	 */
+
+	*(int *)start = 1;
+
+	/* Print header first */
+	if (step == 0)
+		buf += sprintf(buf, "YAFFS built:" __DATE__ " " __TIME__ "\n");
+	else if (step == 1)
+		buf += sprintf(buf, "\n");
+	else {
+		step -= 2;
+
+		mutex_lock(&yaffs_context_lock);
+
+		/* Locate and print the Nth entry.  Order N-squared but N is small. */
+		list_for_each(item, &yaffs_context_list) {
+			struct yaffs_linux_context *dc =
+			    list_entry(item, struct yaffs_linux_context,
+				       context_list);
+			struct yaffs_dev *dev = dc->dev;
+
+			if (n < (step & ~1)) {
+				n += 2;
+				continue;
+			}
+			if ((step & 1) == 0) {
+				buf +=
+				    sprintf(buf, "\nDevice %d \"%s\"\n", n,
+					    dev->param.name);
+				buf = yaffs_dump_dev_part0(buf, dev);
+			} else {
+				buf = yaffs_dump_dev_part1(buf, dev);
+                        }
+
+			break;
+		}
+		mutex_unlock(&yaffs_context_lock);
+	}
+
+	return buf - page < count ? buf - page : count;
+}
+
+
+/**
+ * Set the verbosity of the warnings and error messages.
+ *
+ * Note that the names can only be a..z or _ with the current code.
+ */
+
+static struct {
+	char *mask_name;
+	unsigned mask_bitfield;
+} mask_flags[] = {
+	{"allocate", YAFFS_TRACE_ALLOCATE}, 
+	{"always", YAFFS_TRACE_ALWAYS},
+	{"background", YAFFS_TRACE_BACKGROUND},
+	{"bad_blocks", YAFFS_TRACE_BAD_BLOCKS},
+	{"buffers", YAFFS_TRACE_BUFFERS},
+	{"bug", YAFFS_TRACE_BUG},
+	{"checkpt", YAFFS_TRACE_CHECKPOINT},
+	{"deletion", YAFFS_TRACE_DELETION},
+	{"erase", YAFFS_TRACE_ERASE},
+	{"error", YAFFS_TRACE_ERROR},
+	{"gc_detail", YAFFS_TRACE_GC_DETAIL},
+	{"gc", YAFFS_TRACE_GC},
+	{"lock", YAFFS_TRACE_LOCK},
+	{"mtd", YAFFS_TRACE_MTD},
+	{"nandaccess", YAFFS_TRACE_NANDACCESS},
+	{"os", YAFFS_TRACE_OS},
+	{"scan_debug", YAFFS_TRACE_SCAN_DEBUG},
+	{"scan", YAFFS_TRACE_SCAN},
+	{"mount", YAFFS_TRACE_MOUNT},
+	{"tracing", YAFFS_TRACE_TRACING},
+	{"sync", YAFFS_TRACE_SYNC},
+	{"write", YAFFS_TRACE_WRITE},
+	{"verify", YAFFS_TRACE_VERIFY},
+	{"verify_nand", YAFFS_TRACE_VERIFY_NAND},
+	{"verify_full", YAFFS_TRACE_VERIFY_FULL},
+	{"verify_all", YAFFS_TRACE_VERIFY_ALL},
+	{"all", 0xffffffff},
+	{"none", 0},
+	{NULL, 0},
+};
+
+#define MAX_MASK_NAME_LENGTH 40
+static int yaffs_proc_write_trace_options(struct file *file, const char *buf,
+					  unsigned long count, void *data)
+{
+	unsigned rg = 0, mask_bitfield;
+	char *end;
+	char *mask_name;
+	const char *x;
+	char substring[MAX_MASK_NAME_LENGTH + 1];
+	int i;
+	int done = 0;
+	int add, len = 0;
+	int pos = 0;
+
+	rg = yaffs_trace_mask;
+
+	while (!done && (pos < count)) {
+		done = 1;
+		while ((pos < count) && isspace(buf[pos]))
+			pos++;
+
+		switch (buf[pos]) {
+		case '+':
+		case '-':
+		case '=':
+			add = buf[pos];
+			pos++;
+			break;
+
+		default:
+			add = ' ';
+			break;
+		}
+		mask_name = NULL;
+
+		mask_bitfield = simple_strtoul(buf + pos, &end, 0);
+
+		if (end > buf + pos) {
+			mask_name = "numeral";
+			len = end - (buf + pos);
+			pos += len;
+			done = 0;
+		} else {
+			for (x = buf + pos, i = 0;
+			     (*x == '_' || (*x >= 'a' && *x <= 'z')) &&
+			     i < MAX_MASK_NAME_LENGTH; x++, i++, pos++)
+				substring[i] = *x;
+			substring[i] = '\0';
+
+			for (i = 0; mask_flags[i].mask_name != NULL; i++) {
+				if (strcmp(substring, mask_flags[i].mask_name)
+				    == 0) {
+					mask_name = mask_flags[i].mask_name;
+					mask_bitfield =
+					    mask_flags[i].mask_bitfield;
+					done = 0;
+					break;
+				}
+			}
+		}
+
+		if (mask_name != NULL) {
+			done = 0;
+			switch (add) {
+			case '-':
+				rg &= ~mask_bitfield;
+				break;
+			case '+':
+				rg |= mask_bitfield;
+				break;
+			case '=':
+				rg = mask_bitfield;
+				break;
+			default:
+				rg |= mask_bitfield;
+				break;
+			}
+		}
+	}
+
+	yaffs_trace_mask = rg | YAFFS_TRACE_ALWAYS;
+
+	printk(KERN_DEBUG "new trace = 0x%08X\n", yaffs_trace_mask);
+
+	if (rg & YAFFS_TRACE_ALWAYS) {
+		for (i = 0; mask_flags[i].mask_name != NULL; i++) {
+			char flag;
+			flag = ((rg & mask_flags[i].mask_bitfield) ==
+				mask_flags[i].mask_bitfield) ? '+' : '-';
+			printk(KERN_DEBUG "%c%s\n", flag,
+			       mask_flags[i].mask_name);
+		}
+	}
+
+	return count;
+}
+
+static int yaffs_proc_write(struct file *file, const char *buf,
+			    unsigned long count, void *data)
+{
+	return yaffs_proc_write_trace_options(file, buf, count, data);
+}
+
+/* Stuff to handle installation of file systems */
+struct file_system_to_install {
+	struct file_system_type *fst;
+	int installed;
+};
+
+static struct file_system_to_install fs_to_install[] = {
+	{&yaffs_fs_type, 0},
+	{&yaffs2_fs_type, 0},
+	{NULL, 0}
+};
+
+static int __init init_yaffs_fs(void)
+{
+	int error = 0;
+	struct file_system_to_install *fsinst;
+
+	yaffs_trace(YAFFS_TRACE_ALWAYS,
+		"yaffs built " __DATE__ " " __TIME__ " Installing.");
+
+#ifdef CONFIG_YAFFS_ALWAYS_CHECK_CHUNK_ERASED
+	yaffs_trace(YAFFS_TRACE_ALWAYS,
+		"\n\nYAFFS-WARNING CONFIG_YAFFS_ALWAYS_CHECK_CHUNK_ERASED selected.\n\n\n");
+#endif
+
+	mutex_init(&yaffs_context_lock);
+
+	/* Install the proc_fs entries */
+	my_proc_entry = create_proc_entry("yaffs",
+					  S_IRUGO | S_IFREG, YPROC_ROOT);
+
+	if (my_proc_entry) {
+		my_proc_entry->write_proc = yaffs_proc_write;
+		my_proc_entry->read_proc = yaffs_proc_read;
+		my_proc_entry->data = NULL;
+	} else {
+		return -ENOMEM;
+        }
+
+
+	/* Now add the file system entries */
+
+	fsinst = fs_to_install;
+
+	while (fsinst->fst && !error) {
+		error = register_filesystem(fsinst->fst);
+		if (!error)
+			fsinst->installed = 1;
+		fsinst++;
+	}
+
+	/* Any errors? uninstall  */
+	if (error) {
+		fsinst = fs_to_install;
+
+		while (fsinst->fst) {
+			if (fsinst->installed) {
+				unregister_filesystem(fsinst->fst);
+				fsinst->installed = 0;
+			}
+			fsinst++;
+		}
+	}
+
+	return error;
+}
+
+static void __exit exit_yaffs_fs(void)
+{
+
+	struct file_system_to_install *fsinst;
+
+	yaffs_trace(YAFFS_TRACE_ALWAYS,
+		"yaffs built " __DATE__ " " __TIME__ " removing.");
+
+	remove_proc_entry("yaffs", YPROC_ROOT);
+
+	fsinst = fs_to_install;
+
+	while (fsinst->fst) {
+		if (fsinst->installed) {
+			unregister_filesystem(fsinst->fst);
+			fsinst->installed = 0;
+		}
+		fsinst++;
+	}
+}
+
+module_init(init_yaffs_fs)
+    module_exit(exit_yaffs_fs)
+
+    MODULE_DESCRIPTION("YAFFS2 - a NAND specific flash file system");
+MODULE_AUTHOR("Charles Manning, Aleph One Ltd., 2002-2010");
+MODULE_LICENSE("GPL");
diff --git a/fs/yaffs2/yaffs_yaffs1.c b/fs/yaffs2/yaffs_yaffs1.c
new file mode 100644
index 0000000..9eb6030
--- /dev/null
+++ b/fs/yaffs2/yaffs_yaffs1.c
@@ -0,0 +1,433 @@
+/*
+ * YAFFS: Yet Another Flash File System. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU General Public License version 2 as
+ * published by the Free Software Foundation.
+ */
+
+#include "yaffs_yaffs1.h"
+#include "yportenv.h"
+#include "yaffs_trace.h"
+#include "yaffs_bitmap.h"
+#include "yaffs_getblockinfo.h"
+#include "yaffs_nand.h"
+#include "yaffs_attribs.h"
+
+int yaffs1_scan(struct yaffs_dev *dev)
+{
+	struct yaffs_ext_tags tags;
+	int blk;
+	int result;
+
+	int chunk;
+	int c;
+	int deleted;
+	enum yaffs_block_state state;
+	struct yaffs_obj *hard_list = NULL;
+	struct yaffs_block_info *bi;
+	u32 seq_number;
+	struct yaffs_obj_hdr *oh;
+	struct yaffs_obj *in;
+	struct yaffs_obj *parent;
+
+	int alloc_failed = 0;
+
+	struct yaffs_shadow_fixer *shadow_fixers = NULL;
+
+	u8 *chunk_data;
+
+	yaffs_trace(YAFFS_TRACE_SCAN,
+		"yaffs1_scan starts  intstartblk %d intendblk %d...",
+		dev->internal_start_block, dev->internal_end_block);
+
+	chunk_data = yaffs_get_temp_buffer(dev, __LINE__);
+
+	dev->seq_number = YAFFS_LOWEST_SEQUENCE_NUMBER;
+
+	/* Scan all the blocks to determine their state */
+	bi = dev->block_info;
+	for (blk = dev->internal_start_block; blk <= dev->internal_end_block;
+	     blk++) {
+		yaffs_clear_chunk_bits(dev, blk);
+		bi->pages_in_use = 0;
+		bi->soft_del_pages = 0;
+
+		yaffs_query_init_block_state(dev, blk, &state, &seq_number);
+
+		bi->block_state = state;
+		bi->seq_number = seq_number;
+
+		if (bi->seq_number == YAFFS_SEQUENCE_BAD_BLOCK)
+			bi->block_state = state = YAFFS_BLOCK_STATE_DEAD;
+
+		yaffs_trace(YAFFS_TRACE_SCAN_DEBUG,
+			"Block scanning block %d state %d seq %d",
+			blk, state, seq_number);
+
+		if (state == YAFFS_BLOCK_STATE_DEAD) {
+			yaffs_trace(YAFFS_TRACE_BAD_BLOCKS,
+				"block %d is bad", blk);
+		} else if (state == YAFFS_BLOCK_STATE_EMPTY) {
+			yaffs_trace(YAFFS_TRACE_SCAN_DEBUG, "Block empty ");
+			dev->n_erased_blocks++;
+			dev->n_free_chunks += dev->param.chunks_per_block;
+		}
+		bi++;
+	}
+
+	/* For each block.... */
+	for (blk = dev->internal_start_block;
+	     !alloc_failed && blk <= dev->internal_end_block; blk++) {
+
+		cond_resched();
+
+		bi = yaffs_get_block_info(dev, blk);
+		state = bi->block_state;
+
+		deleted = 0;
+
+		/* For each chunk in each block that needs scanning.... */
+		for (c = 0; !alloc_failed && c < dev->param.chunks_per_block &&
+		     state == YAFFS_BLOCK_STATE_NEEDS_SCANNING; c++) {
+			/* Read the tags and decide what to do */
+			chunk = blk * dev->param.chunks_per_block + c;
+
+			result = yaffs_rd_chunk_tags_nand(dev, chunk, NULL,
+							  &tags);
+
+			/* Let's have a good look at this chunk... */
+
+			if (tags.ecc_result == YAFFS_ECC_RESULT_UNFIXED
+			    || tags.is_deleted) {
+				/* YAFFS1 only...
+				 * A deleted chunk
+				 */
+				deleted++;
+				dev->n_free_chunks++;
+				/*T((" %d %d deleted\n",blk,c)); */
+			} else if (!tags.chunk_used) {
+				/* An unassigned chunk in the block
+				 * This means that either the block is empty or
+				 * this is the one being allocated from
+				 */
+
+				if (c == 0) {
+					/* We're looking at the first chunk in the block so the block is unused */
+					state = YAFFS_BLOCK_STATE_EMPTY;
+					dev->n_erased_blocks++;
+				} else {
+					/* this is the block being allocated from */
+					yaffs_trace(YAFFS_TRACE_SCAN,
+						" Allocating from %d %d",
+						blk, c);
+					state = YAFFS_BLOCK_STATE_ALLOCATING;
+					dev->alloc_block = blk;
+					dev->alloc_page = c;
+					dev->alloc_block_finder = blk;
+					/* Set block finder here to encourage the allocator to go forth from here. */
+
+				}
+
+				dev->n_free_chunks +=
+				    (dev->param.chunks_per_block - c);
+			} else if (tags.chunk_id > 0) {
+				/* chunk_id > 0 so it is a data chunk... */
+				unsigned int endpos;
+
+				yaffs_set_chunk_bit(dev, blk, c);
+				bi->pages_in_use++;
+
+				in = yaffs_find_or_create_by_number(dev,
+								    tags.obj_id,
+								    YAFFS_OBJECT_TYPE_FILE);
+				/* PutChunkIntoFile checks for a clash (two data chunks with
+				 * the same chunk_id).
+				 */
+
+				if (!in)
+					alloc_failed = 1;
+
+				if (in) {
+					if (!yaffs_put_chunk_in_file
+					    (in, tags.chunk_id, chunk, 1))
+						alloc_failed = 1;
+				}
+
+				endpos =
+				    (tags.chunk_id -
+				     1) * dev->data_bytes_per_chunk +
+				    tags.n_bytes;
+				if (in
+				    && in->variant_type ==
+				    YAFFS_OBJECT_TYPE_FILE
+				    && in->variant.file_variant.scanned_size <
+				    endpos) {
+					in->variant.file_variant.scanned_size =
+					    endpos;
+					if (!dev->param.use_header_file_size) {
+						in->variant.
+						    file_variant.file_size =
+						    in->variant.
+						    file_variant.scanned_size;
+					}
+
+				}
+				/* T((" %d %d data %d %d\n",blk,c,tags.obj_id,tags.chunk_id));   */
+			} else {
+				/* chunk_id == 0, so it is an ObjectHeader.
+				 * Thus, we read in the object header and make the object
+				 */
+				yaffs_set_chunk_bit(dev, blk, c);
+				bi->pages_in_use++;
+
+				result = yaffs_rd_chunk_tags_nand(dev, chunk,
+								  chunk_data,
+								  NULL);
+
+				oh = (struct yaffs_obj_hdr *)chunk_data;
+
+				in = yaffs_find_by_number(dev, tags.obj_id);
+				if (in && in->variant_type != oh->type) {
+					/* This should not happen, but somehow
+					 * Wev'e ended up with an obj_id that has been reused but not yet
+					 * deleted, and worse still it has changed type. Delete the old object.
+					 */
+
+					yaffs_del_obj(in);
+
+					in = 0;
+				}
+
+				in = yaffs_find_or_create_by_number(dev,
+								    tags.obj_id,
+								    oh->type);
+
+				if (!in)
+					alloc_failed = 1;
+
+				if (in && oh->shadows_obj > 0) {
+
+					struct yaffs_shadow_fixer *fixer;
+					fixer =
+						kmalloc(sizeof
+						(struct yaffs_shadow_fixer),
+						GFP_NOFS);
+					if (fixer) {
+						fixer->next = shadow_fixers;
+						shadow_fixers = fixer;
+						fixer->obj_id = tags.obj_id;
+						fixer->shadowed_id =
+						    oh->shadows_obj;
+						yaffs_trace(YAFFS_TRACE_SCAN,
+							" Shadow fixer: %d shadows %d",
+							fixer->obj_id,
+							fixer->shadowed_id);
+
+					}
+
+				}
+
+				if (in && in->valid) {
+					/* We have already filled this one. We have a duplicate and need to resolve it. */
+
+					unsigned existing_serial = in->serial;
+					unsigned new_serial =
+					    tags.serial_number;
+
+					if (((existing_serial + 1) & 3) ==
+					    new_serial) {
+						/* Use new one - destroy the exisiting one */
+						yaffs_chunk_del(dev,
+								in->hdr_chunk,
+								1, __LINE__);
+						in->valid = 0;
+					} else {
+						/* Use existing - destroy this one. */
+						yaffs_chunk_del(dev, chunk, 1,
+								__LINE__);
+					}
+				}
+
+				if (in && !in->valid &&
+				    (tags.obj_id == YAFFS_OBJECTID_ROOT ||
+				     tags.obj_id ==
+				     YAFFS_OBJECTID_LOSTNFOUND)) {
+					/* We only load some info, don't fiddle with directory structure */
+					in->valid = 1;
+					in->variant_type = oh->type;
+
+					in->yst_mode = oh->yst_mode;
+					yaffs_load_attribs(in, oh);
+					in->hdr_chunk = chunk;
+					in->serial = tags.serial_number;
+
+				} else if (in && !in->valid) {
+					/* we need to load this info */
+
+					in->valid = 1;
+					in->variant_type = oh->type;
+
+					in->yst_mode = oh->yst_mode;
+					yaffs_load_attribs(in, oh);
+					in->hdr_chunk = chunk;
+					in->serial = tags.serial_number;
+
+					yaffs_set_obj_name_from_oh(in, oh);
+					in->dirty = 0;
+
+					/* directory stuff...
+					 * hook up to parent
+					 */
+
+					parent =
+					    yaffs_find_or_create_by_number
+					    (dev, oh->parent_obj_id,
+					     YAFFS_OBJECT_TYPE_DIRECTORY);
+					if (!parent)
+						alloc_failed = 1;
+					if (parent && parent->variant_type ==
+					    YAFFS_OBJECT_TYPE_UNKNOWN) {
+						/* Set up as a directory */
+						parent->variant_type =
+						    YAFFS_OBJECT_TYPE_DIRECTORY;
+						INIT_LIST_HEAD(&parent->
+							       variant.dir_variant.children);
+					} else if (!parent
+						   || parent->variant_type !=
+						   YAFFS_OBJECT_TYPE_DIRECTORY) {
+						/* Hoosterman, another problem....
+						 * We're trying to use a non-directory as a directory
+						 */
+
+						yaffs_trace(YAFFS_TRACE_ERROR,
+							"yaffs tragedy: attempting to use non-directory as a directory in scan. Put in lost+found."
+							);
+						parent = dev->lost_n_found;
+					}
+
+					yaffs_add_obj_to_dir(parent, in);
+
+					if (0 && (parent == dev->del_dir ||
+						  parent ==
+						  dev->unlinked_dir)) {
+						in->deleted = 1;	/* If it is unlinked at start up then it wants deleting */
+						dev->n_deleted_files++;
+					}
+					/* Note re hardlinks.
+					 * Since we might scan a hardlink before its equivalent object is scanned
+					 * we put them all in a list.
+					 * After scanning is complete, we should have all the objects, so we run through this
+					 * list and fix up all the chains.
+					 */
+
+					switch (in->variant_type) {
+					case YAFFS_OBJECT_TYPE_UNKNOWN:
+						/* Todo got a problem */
+						break;
+					case YAFFS_OBJECT_TYPE_FILE:
+						if (dev->param.
+						    use_header_file_size)
+
+							in->variant.
+							    file_variant.file_size
+							    = oh->file_size;
+
+						break;
+					case YAFFS_OBJECT_TYPE_HARDLINK:
+						in->variant.
+						    hardlink_variant.equiv_id =
+						    oh->equiv_id;
+						in->hard_links.next =
+						    (struct list_head *)
+						    hard_list;
+						hard_list = in;
+						break;
+					case YAFFS_OBJECT_TYPE_DIRECTORY:
+						/* Do nothing */
+						break;
+					case YAFFS_OBJECT_TYPE_SPECIAL:
+						/* Do nothing */
+						break;
+					case YAFFS_OBJECT_TYPE_SYMLINK:
+						in->variant.symlink_variant.
+						    alias =
+						    yaffs_clone_str(oh->alias);
+						if (!in->variant.
+						    symlink_variant.alias)
+							alloc_failed = 1;
+						break;
+					}
+
+				}
+			}
+		}
+
+		if (state == YAFFS_BLOCK_STATE_NEEDS_SCANNING) {
+			/* If we got this far while scanning, then the block is fully allocated. */
+			state = YAFFS_BLOCK_STATE_FULL;
+		}
+
+		if (state == YAFFS_BLOCK_STATE_ALLOCATING) {
+			/* If the block was partially allocated then treat it as fully allocated. */
+			state = YAFFS_BLOCK_STATE_FULL;
+			dev->alloc_block = -1;
+		}
+
+		bi->block_state = state;
+
+		/* Now let's see if it was dirty */
+		if (bi->pages_in_use == 0 &&
+		    !bi->has_shrink_hdr &&
+		    bi->block_state == YAFFS_BLOCK_STATE_FULL) {
+			yaffs_block_became_dirty(dev, blk);
+		}
+
+	}
+
+	/* Ok, we've done all the scanning.
+	 * Fix up the hard link chains.
+	 * We should now have scanned all the objects, now it's time to add these
+	 * hardlinks.
+	 */
+
+	yaffs_link_fixup(dev, hard_list);
+
+	/* Fix up any shadowed objects */
+	{
+		struct yaffs_shadow_fixer *fixer;
+		struct yaffs_obj *obj;
+
+		while (shadow_fixers) {
+			fixer = shadow_fixers;
+			shadow_fixers = fixer->next;
+			/* Complete the rename transaction by deleting the shadowed object
+			 * then setting the object header to unshadowed.
+			 */
+			obj = yaffs_find_by_number(dev, fixer->shadowed_id);
+			if (obj)
+				yaffs_del_obj(obj);
+
+			obj = yaffs_find_by_number(dev, fixer->obj_id);
+
+			if (obj)
+				yaffs_update_oh(obj, NULL, 1, 0, 0, NULL);
+
+			kfree(fixer);
+		}
+	}
+
+	yaffs_release_temp_buffer(dev, chunk_data, __LINE__);
+
+	if (alloc_failed)
+		return YAFFS_FAIL;
+
+	yaffs_trace(YAFFS_TRACE_SCAN, "yaffs1_scan ends");
+
+	return YAFFS_OK;
+}
diff --git a/fs/yaffs2/yaffs_yaffs1.h b/fs/yaffs2/yaffs_yaffs1.h
new file mode 100644
index 0000000..db23e04
--- /dev/null
+++ b/fs/yaffs2/yaffs_yaffs1.h
@@ -0,0 +1,22 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+#ifndef __YAFFS_YAFFS1_H__
+#define __YAFFS_YAFFS1_H__
+
+#include "yaffs_guts.h"
+int yaffs1_scan(struct yaffs_dev *dev);
+
+#endif
diff --git a/fs/yaffs2/yaffs_yaffs2.c b/fs/yaffs2/yaffs_yaffs2.c
new file mode 100644
index 0000000..33397af
--- /dev/null
+++ b/fs/yaffs2/yaffs_yaffs2.c
@@ -0,0 +1,1598 @@
+/*
+ * YAFFS: Yet Another Flash File System. A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU General Public License version 2 as
+ * published by the Free Software Foundation.
+ */
+
+#include "yaffs_guts.h"
+#include "yaffs_trace.h"
+#include "yaffs_yaffs2.h"
+#include "yaffs_checkptrw.h"
+#include "yaffs_bitmap.h"
+#include "yaffs_nand.h"
+#include "yaffs_getblockinfo.h"
+#include "yaffs_verify.h"
+#include "yaffs_attribs.h"
+
+/*
+ * Checkpoints are really no benefit on very small partitions.
+ *
+ * To save space on small partitions don't bother with checkpoints unless
+ * the partition is at least this big.
+ */
+#define YAFFS_CHECKPOINT_MIN_BLOCKS 60
+
+#define YAFFS_SMALL_HOLE_THRESHOLD 4
+
+/*
+ * Oldest Dirty Sequence Number handling.
+ */
+
+/* yaffs_calc_oldest_dirty_seq()
+ * yaffs2_find_oldest_dirty_seq()
+ * Calculate the oldest dirty sequence number if we don't know it.
+ */
+void yaffs_calc_oldest_dirty_seq(struct yaffs_dev *dev)
+{
+	int i;
+	unsigned seq;
+	unsigned block_no = 0;
+	struct yaffs_block_info *b;
+
+	if (!dev->param.is_yaffs2)
+		return;
+
+	/* Find the oldest dirty sequence number. */
+	seq = dev->seq_number + 1;
+	b = dev->block_info;
+	for (i = dev->internal_start_block; i <= dev->internal_end_block; i++) {
+		if (b->block_state == YAFFS_BLOCK_STATE_FULL &&
+		    (b->pages_in_use - b->soft_del_pages) <
+		    dev->param.chunks_per_block && b->seq_number < seq) {
+			seq = b->seq_number;
+			block_no = i;
+		}
+		b++;
+	}
+
+	if (block_no) {
+		dev->oldest_dirty_seq = seq;
+		dev->oldest_dirty_block = block_no;
+	}
+
+}
+
+void yaffs2_find_oldest_dirty_seq(struct yaffs_dev *dev)
+{
+	if (!dev->param.is_yaffs2)
+		return;
+
+	if (!dev->oldest_dirty_seq)
+		yaffs_calc_oldest_dirty_seq(dev);
+}
+
+/*
+ * yaffs_clear_oldest_dirty_seq()
+ * Called when a block is erased or marked bad. (ie. when its seq_number
+ * becomes invalid). If the value matches the oldest then we clear 
+ * dev->oldest_dirty_seq to force its recomputation.
+ */
+void yaffs2_clear_oldest_dirty_seq(struct yaffs_dev *dev,
+				   struct yaffs_block_info *bi)
+{
+
+	if (!dev->param.is_yaffs2)
+		return;
+
+	if (!bi || bi->seq_number == dev->oldest_dirty_seq) {
+		dev->oldest_dirty_seq = 0;
+		dev->oldest_dirty_block = 0;
+	}
+}
+
+/*
+ * yaffs2_update_oldest_dirty_seq()
+ * Update the oldest dirty sequence number whenever we dirty a block.
+ * Only do this if the oldest_dirty_seq is actually being tracked.
+ */
+void yaffs2_update_oldest_dirty_seq(struct yaffs_dev *dev, unsigned block_no,
+				    struct yaffs_block_info *bi)
+{
+	if (!dev->param.is_yaffs2)
+		return;
+
+	if (dev->oldest_dirty_seq) {
+		if (dev->oldest_dirty_seq > bi->seq_number) {
+			dev->oldest_dirty_seq = bi->seq_number;
+			dev->oldest_dirty_block = block_no;
+		}
+	}
+}
+
+int yaffs_block_ok_for_gc(struct yaffs_dev *dev, struct yaffs_block_info *bi)
+{
+
+	if (!dev->param.is_yaffs2)
+		return 1;	/* disqualification only applies to yaffs2. */
+
+	if (!bi->has_shrink_hdr)
+		return 1;	/* can gc */
+
+	yaffs2_find_oldest_dirty_seq(dev);
+
+	/* Can't do gc of this block if there are any blocks older than this one that have
+	 * discarded pages.
+	 */
+	return (bi->seq_number <= dev->oldest_dirty_seq);
+}
+
+/*
+ * yaffs2_find_refresh_block()
+ * periodically finds the oldest full block by sequence number for refreshing.
+ * Only for yaffs2.
+ */
+u32 yaffs2_find_refresh_block(struct yaffs_dev * dev)
+{
+	u32 b;
+
+	u32 oldest = 0;
+	u32 oldest_seq = 0;
+
+	struct yaffs_block_info *bi;
+
+	if (!dev->param.is_yaffs2)
+		return oldest;
+
+	/*
+	 * If refresh period < 10 then refreshing is disabled.
+	 */
+	if (dev->param.refresh_period < 10)
+		return oldest;
+
+	/*
+	 * Fix broken values.
+	 */
+	if (dev->refresh_skip > dev->param.refresh_period)
+		dev->refresh_skip = dev->param.refresh_period;
+
+	if (dev->refresh_skip > 0)
+		return oldest;
+
+	/*
+	 * Refresh skip is now zero.
+	 * We'll do a refresh this time around....
+	 * Update the refresh skip and find the oldest block.
+	 */
+	dev->refresh_skip = dev->param.refresh_period;
+	dev->refresh_count++;
+	bi = dev->block_info;
+	for (b = dev->internal_start_block; b <= dev->internal_end_block; b++) {
+
+		if (bi->block_state == YAFFS_BLOCK_STATE_FULL) {
+
+			if (oldest < 1 || bi->seq_number < oldest_seq) {
+				oldest = b;
+				oldest_seq = bi->seq_number;
+			}
+		}
+		bi++;
+	}
+
+	if (oldest > 0) {
+		yaffs_trace(YAFFS_TRACE_GC,
+			"GC refresh count %d selected block %d with seq_number %d",
+			dev->refresh_count, oldest, oldest_seq);
+	}
+
+	return oldest;
+}
+
+int yaffs2_checkpt_required(struct yaffs_dev *dev)
+{
+	int nblocks;
+
+	if (!dev->param.is_yaffs2)
+		return 0;
+
+	nblocks = dev->internal_end_block - dev->internal_start_block + 1;
+
+	return !dev->param.skip_checkpt_wr &&
+	    !dev->read_only && (nblocks >= YAFFS_CHECKPOINT_MIN_BLOCKS);
+}
+
+int yaffs_calc_checkpt_blocks_required(struct yaffs_dev *dev)
+{
+	int retval;
+
+	if (!dev->param.is_yaffs2)
+		return 0;
+
+	if (!dev->checkpoint_blocks_required && yaffs2_checkpt_required(dev)) {
+		/* Not a valid value so recalculate */
+		int n_bytes = 0;
+		int n_blocks;
+		int dev_blocks =
+		    (dev->param.end_block - dev->param.start_block + 1);
+
+		n_bytes += sizeof(struct yaffs_checkpt_validity);
+		n_bytes += sizeof(struct yaffs_checkpt_dev);
+		n_bytes += dev_blocks * sizeof(struct yaffs_block_info);
+		n_bytes += dev_blocks * dev->chunk_bit_stride;
+		n_bytes +=
+		    (sizeof(struct yaffs_checkpt_obj) +
+		     sizeof(u32)) * (dev->n_obj);
+		n_bytes += (dev->tnode_size + sizeof(u32)) * (dev->n_tnodes);
+		n_bytes += sizeof(struct yaffs_checkpt_validity);
+		n_bytes += sizeof(u32);	/* checksum */
+
+		/* Round up and add 2 blocks to allow for some bad blocks, so add 3 */
+
+		n_blocks =
+		    (n_bytes /
+		     (dev->data_bytes_per_chunk *
+		      dev->param.chunks_per_block)) + 3;
+
+		dev->checkpoint_blocks_required = n_blocks;
+	}
+
+	retval = dev->checkpoint_blocks_required - dev->blocks_in_checkpt;
+	if (retval < 0)
+		retval = 0;
+	return retval;
+}
+
+/*--------------------- Checkpointing --------------------*/
+
+static int yaffs2_wr_checkpt_validity_marker(struct yaffs_dev *dev, int head)
+{
+	struct yaffs_checkpt_validity cp;
+
+	memset(&cp, 0, sizeof(cp));
+
+	cp.struct_type = sizeof(cp);
+	cp.magic = YAFFS_MAGIC;
+	cp.version = YAFFS_CHECKPOINT_VERSION;
+	cp.head = (head) ? 1 : 0;
+
+	return (yaffs2_checkpt_wr(dev, &cp, sizeof(cp)) == sizeof(cp)) ? 1 : 0;
+}
+
+static int yaffs2_rd_checkpt_validity_marker(struct yaffs_dev *dev, int head)
+{
+	struct yaffs_checkpt_validity cp;
+	int ok;
+
+	ok = (yaffs2_checkpt_rd(dev, &cp, sizeof(cp)) == sizeof(cp));
+
+	if (ok)
+		ok = (cp.struct_type == sizeof(cp)) &&
+		    (cp.magic == YAFFS_MAGIC) &&
+		    (cp.version == YAFFS_CHECKPOINT_VERSION) &&
+		    (cp.head == ((head) ? 1 : 0));
+	return ok ? 1 : 0;
+}
+
+static void yaffs2_dev_to_checkpt_dev(struct yaffs_checkpt_dev *cp,
+				      struct yaffs_dev *dev)
+{
+	cp->n_erased_blocks = dev->n_erased_blocks;
+	cp->alloc_block = dev->alloc_block;
+	cp->alloc_page = dev->alloc_page;
+	cp->n_free_chunks = dev->n_free_chunks;
+
+	cp->n_deleted_files = dev->n_deleted_files;
+	cp->n_unlinked_files = dev->n_unlinked_files;
+	cp->n_bg_deletions = dev->n_bg_deletions;
+	cp->seq_number = dev->seq_number;
+
+}
+
+static void yaffs_checkpt_dev_to_dev(struct yaffs_dev *dev,
+				     struct yaffs_checkpt_dev *cp)
+{
+	dev->n_erased_blocks = cp->n_erased_blocks;
+	dev->alloc_block = cp->alloc_block;
+	dev->alloc_page = cp->alloc_page;
+	dev->n_free_chunks = cp->n_free_chunks;
+
+	dev->n_deleted_files = cp->n_deleted_files;
+	dev->n_unlinked_files = cp->n_unlinked_files;
+	dev->n_bg_deletions = cp->n_bg_deletions;
+	dev->seq_number = cp->seq_number;
+}
+
+static int yaffs2_wr_checkpt_dev(struct yaffs_dev *dev)
+{
+	struct yaffs_checkpt_dev cp;
+	u32 n_bytes;
+	u32 n_blocks =
+	    (dev->internal_end_block - dev->internal_start_block + 1);
+
+	int ok;
+
+	/* Write device runtime values */
+	yaffs2_dev_to_checkpt_dev(&cp, dev);
+	cp.struct_type = sizeof(cp);
+
+	ok = (yaffs2_checkpt_wr(dev, &cp, sizeof(cp)) == sizeof(cp));
+
+	/* Write block info */
+	if (ok) {
+		n_bytes = n_blocks * sizeof(struct yaffs_block_info);
+		ok = (yaffs2_checkpt_wr(dev, dev->block_info, n_bytes) ==
+		      n_bytes);
+	}
+
+	/* Write chunk bits */
+	if (ok) {
+		n_bytes = n_blocks * dev->chunk_bit_stride;
+		ok = (yaffs2_checkpt_wr(dev, dev->chunk_bits, n_bytes) ==
+		      n_bytes);
+	}
+	return ok ? 1 : 0;
+
+}
+
+static int yaffs2_rd_checkpt_dev(struct yaffs_dev *dev)
+{
+	struct yaffs_checkpt_dev cp;
+	u32 n_bytes;
+	u32 n_blocks =
+	    (dev->internal_end_block - dev->internal_start_block + 1);
+
+	int ok;
+
+	ok = (yaffs2_checkpt_rd(dev, &cp, sizeof(cp)) == sizeof(cp));
+	if (!ok)
+		return 0;
+
+	if (cp.struct_type != sizeof(cp))
+		return 0;
+
+	yaffs_checkpt_dev_to_dev(dev, &cp);
+
+	n_bytes = n_blocks * sizeof(struct yaffs_block_info);
+
+	ok = (yaffs2_checkpt_rd(dev, dev->block_info, n_bytes) == n_bytes);
+
+	if (!ok)
+		return 0;
+	n_bytes = n_blocks * dev->chunk_bit_stride;
+
+	ok = (yaffs2_checkpt_rd(dev, dev->chunk_bits, n_bytes) == n_bytes);
+
+	return ok ? 1 : 0;
+}
+
+static void yaffs2_obj_checkpt_obj(struct yaffs_checkpt_obj *cp,
+				   struct yaffs_obj *obj)
+{
+
+	cp->obj_id = obj->obj_id;
+	cp->parent_id = (obj->parent) ? obj->parent->obj_id : 0;
+	cp->hdr_chunk = obj->hdr_chunk;
+	cp->variant_type = obj->variant_type;
+	cp->deleted = obj->deleted;
+	cp->soft_del = obj->soft_del;
+	cp->unlinked = obj->unlinked;
+	cp->fake = obj->fake;
+	cp->rename_allowed = obj->rename_allowed;
+	cp->unlink_allowed = obj->unlink_allowed;
+	cp->serial = obj->serial;
+	cp->n_data_chunks = obj->n_data_chunks;
+
+	if (obj->variant_type == YAFFS_OBJECT_TYPE_FILE)
+		cp->size_or_equiv_obj = obj->variant.file_variant.file_size;
+	else if (obj->variant_type == YAFFS_OBJECT_TYPE_HARDLINK)
+		cp->size_or_equiv_obj = obj->variant.hardlink_variant.equiv_id;
+}
+
+static int taffs2_checkpt_obj_to_obj(struct yaffs_obj *obj,
+				     struct yaffs_checkpt_obj *cp)
+{
+
+	struct yaffs_obj *parent;
+
+	if (obj->variant_type != cp->variant_type) {
+		yaffs_trace(YAFFS_TRACE_ERROR,
+			"Checkpoint read object %d type %d chunk %d does not match existing object type %d",
+			cp->obj_id, cp->variant_type, cp->hdr_chunk,
+			obj->variant_type);
+		return 0;
+	}
+
+	obj->obj_id = cp->obj_id;
+
+	if (cp->parent_id)
+		parent = yaffs_find_or_create_by_number(obj->my_dev,
+							cp->parent_id,
+							YAFFS_OBJECT_TYPE_DIRECTORY);
+	else
+		parent = NULL;
+
+	if (parent) {
+		if (parent->variant_type != YAFFS_OBJECT_TYPE_DIRECTORY) {
+			yaffs_trace(YAFFS_TRACE_ALWAYS,
+				"Checkpoint read object %d parent %d type %d chunk %d Parent type, %d, not directory",
+				cp->obj_id, cp->parent_id,
+				cp->variant_type, cp->hdr_chunk,
+				parent->variant_type);
+			return 0;
+		}
+		yaffs_add_obj_to_dir(parent, obj);
+	}
+
+	obj->hdr_chunk = cp->hdr_chunk;
+	obj->variant_type = cp->variant_type;
+	obj->deleted = cp->deleted;
+	obj->soft_del = cp->soft_del;
+	obj->unlinked = cp->unlinked;
+	obj->fake = cp->fake;
+	obj->rename_allowed = cp->rename_allowed;
+	obj->unlink_allowed = cp->unlink_allowed;
+	obj->serial = cp->serial;
+	obj->n_data_chunks = cp->n_data_chunks;
+
+	if (obj->variant_type == YAFFS_OBJECT_TYPE_FILE)
+		obj->variant.file_variant.file_size = cp->size_or_equiv_obj;
+	else if (obj->variant_type == YAFFS_OBJECT_TYPE_HARDLINK)
+		obj->variant.hardlink_variant.equiv_id = cp->size_or_equiv_obj;
+
+	if (obj->hdr_chunk > 0)
+		obj->lazy_loaded = 1;
+	return 1;
+}
+
+static int yaffs2_checkpt_tnode_worker(struct yaffs_obj *in,
+				       struct yaffs_tnode *tn, u32 level,
+				       int chunk_offset)
+{
+	int i;
+	struct yaffs_dev *dev = in->my_dev;
+	int ok = 1;
+
+	if (tn) {
+		if (level > 0) {
+
+			for (i = 0; i < YAFFS_NTNODES_INTERNAL && ok; i++) {
+				if (tn->internal[i]) {
+					ok = yaffs2_checkpt_tnode_worker(in,
+									 tn->
+									 internal
+									 [i],
+									 level -
+									 1,
+									 (chunk_offset
+									  <<
+									  YAFFS_TNODES_INTERNAL_BITS)
+									 + i);
+				}
+			}
+		} else if (level == 0) {
+			u32 base_offset =
+			    chunk_offset << YAFFS_TNODES_LEVEL0_BITS;
+			ok = (yaffs2_checkpt_wr
+			      (dev, &base_offset,
+			       sizeof(base_offset)) == sizeof(base_offset));
+			if (ok)
+				ok = (yaffs2_checkpt_wr
+				      (dev, tn,
+				       dev->tnode_size) == dev->tnode_size);
+		}
+	}
+
+	return ok;
+
+}
+
+static int yaffs2_wr_checkpt_tnodes(struct yaffs_obj *obj)
+{
+	u32 end_marker = ~0;
+	int ok = 1;
+
+	if (obj->variant_type == YAFFS_OBJECT_TYPE_FILE) {
+		ok = yaffs2_checkpt_tnode_worker(obj,
+						 obj->variant.file_variant.top,
+						 obj->variant.file_variant.
+						 top_level, 0);
+		if (ok)
+			ok = (yaffs2_checkpt_wr
+			      (obj->my_dev, &end_marker,
+			       sizeof(end_marker)) == sizeof(end_marker));
+	}
+
+	return ok ? 1 : 0;
+}
+
+static int yaffs2_rd_checkpt_tnodes(struct yaffs_obj *obj)
+{
+	u32 base_chunk;
+	int ok = 1;
+	struct yaffs_dev *dev = obj->my_dev;
+	struct yaffs_file_var *file_stuct_ptr = &obj->variant.file_variant;
+	struct yaffs_tnode *tn;
+	int nread = 0;
+
+	ok = (yaffs2_checkpt_rd(dev, &base_chunk, sizeof(base_chunk)) ==
+	      sizeof(base_chunk));
+
+	while (ok && (~base_chunk)) {
+		nread++;
+		/* Read level 0 tnode */
+
+		tn = yaffs_get_tnode(dev);
+		if (tn) {
+			ok = (yaffs2_checkpt_rd(dev, tn, dev->tnode_size) ==
+			      dev->tnode_size);
+		} else {
+			ok = 0;
+                }
+
+		if (tn && ok)
+			ok = yaffs_add_find_tnode_0(dev,
+						    file_stuct_ptr,
+						    base_chunk, tn) ? 1 : 0;
+
+		if (ok)
+			ok = (yaffs2_checkpt_rd
+			      (dev, &base_chunk,
+			       sizeof(base_chunk)) == sizeof(base_chunk));
+
+	}
+
+	yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+		"Checkpoint read tnodes %d records, last %d. ok %d",
+		nread, base_chunk, ok);
+
+	return ok ? 1 : 0;
+}
+
+static int yaffs2_wr_checkpt_objs(struct yaffs_dev *dev)
+{
+	struct yaffs_obj *obj;
+	struct yaffs_checkpt_obj cp;
+	int i;
+	int ok = 1;
+	struct list_head *lh;
+
+	/* Iterate through the objects in each hash entry,
+	 * dumping them to the checkpointing stream.
+	 */
+
+	for (i = 0; ok && i < YAFFS_NOBJECT_BUCKETS; i++) {
+		list_for_each(lh, &dev->obj_bucket[i].list) {
+			if (lh) {
+				obj =
+				    list_entry(lh, struct yaffs_obj, hash_link);
+				if (!obj->defered_free) {
+					yaffs2_obj_checkpt_obj(&cp, obj);
+					cp.struct_type = sizeof(cp);
+
+					yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+						"Checkpoint write object %d parent %d type %d chunk %d obj addr %p",
+						cp.obj_id, cp.parent_id,
+						cp.variant_type, cp.hdr_chunk, obj);
+
+					ok = (yaffs2_checkpt_wr
+					      (dev, &cp,
+					       sizeof(cp)) == sizeof(cp));
+
+					if (ok
+					    && obj->variant_type ==
+					    YAFFS_OBJECT_TYPE_FILE)
+						ok = yaffs2_wr_checkpt_tnodes
+						    (obj);
+				}
+			}
+		}
+	}
+
+	/* Dump end of list */
+	memset(&cp, 0xFF, sizeof(struct yaffs_checkpt_obj));
+	cp.struct_type = sizeof(cp);
+
+	if (ok)
+		ok = (yaffs2_checkpt_wr(dev, &cp, sizeof(cp)) == sizeof(cp));
+
+	return ok ? 1 : 0;
+}
+
+static int yaffs2_rd_checkpt_objs(struct yaffs_dev *dev)
+{
+	struct yaffs_obj *obj;
+	struct yaffs_checkpt_obj cp;
+	int ok = 1;
+	int done = 0;
+	struct yaffs_obj *hard_list = NULL;
+
+	while (ok && !done) {
+		ok = (yaffs2_checkpt_rd(dev, &cp, sizeof(cp)) == sizeof(cp));
+		if (cp.struct_type != sizeof(cp)) {
+			yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+				"struct size %d instead of %d ok %d",
+				cp.struct_type, (int)sizeof(cp), ok);
+			ok = 0;
+		}
+
+		yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+			"Checkpoint read object %d parent %d type %d chunk %d ",
+			cp.obj_id, cp.parent_id, cp.variant_type,
+			cp.hdr_chunk);
+
+		if (ok && cp.obj_id == ~0) {
+			done = 1;
+		} else if (ok) {
+			obj =
+			    yaffs_find_or_create_by_number(dev, cp.obj_id,
+							   cp.variant_type);
+			if (obj) {
+				ok = taffs2_checkpt_obj_to_obj(obj, &cp);
+				if (!ok)
+					break;
+				if (obj->variant_type == YAFFS_OBJECT_TYPE_FILE) {
+					ok = yaffs2_rd_checkpt_tnodes(obj);
+				} else if (obj->variant_type ==
+					   YAFFS_OBJECT_TYPE_HARDLINK) {
+					obj->hard_links.next =
+					    (struct list_head *)hard_list;
+					hard_list = obj;
+				}
+			} else {
+				ok = 0;
+                        }
+		}
+	}
+
+	if (ok)
+		yaffs_link_fixup(dev, hard_list);
+
+	return ok ? 1 : 0;
+}
+
+static int yaffs2_wr_checkpt_sum(struct yaffs_dev *dev)
+{
+	u32 checkpt_sum;
+	int ok;
+
+	yaffs2_get_checkpt_sum(dev, &checkpt_sum);
+
+	ok = (yaffs2_checkpt_wr(dev, &checkpt_sum, sizeof(checkpt_sum)) ==
+	      sizeof(checkpt_sum));
+
+	if (!ok)
+		return 0;
+
+	return 1;
+}
+
+static int yaffs2_rd_checkpt_sum(struct yaffs_dev *dev)
+{
+	u32 checkpt_sum0;
+	u32 checkpt_sum1;
+	int ok;
+
+	yaffs2_get_checkpt_sum(dev, &checkpt_sum0);
+
+	ok = (yaffs2_checkpt_rd(dev, &checkpt_sum1, sizeof(checkpt_sum1)) ==
+	      sizeof(checkpt_sum1));
+
+	if (!ok)
+		return 0;
+
+	if (checkpt_sum0 != checkpt_sum1)
+		return 0;
+
+	return 1;
+}
+
+static int yaffs2_wr_checkpt_data(struct yaffs_dev *dev)
+{
+	int ok = 1;
+
+	if (!yaffs2_checkpt_required(dev)) {
+		yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+			"skipping checkpoint write");
+		ok = 0;
+	}
+
+	if (ok)
+		ok = yaffs2_checkpt_open(dev, 1);
+
+	if (ok) {
+		yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+			"write checkpoint validity");
+		ok = yaffs2_wr_checkpt_validity_marker(dev, 1);
+	}
+	if (ok) {
+		yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+			"write checkpoint device");
+		ok = yaffs2_wr_checkpt_dev(dev);
+	}
+	if (ok) {
+		yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+			"write checkpoint objects");
+		ok = yaffs2_wr_checkpt_objs(dev);
+	}
+	if (ok) {
+		yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+			"write checkpoint validity");
+		ok = yaffs2_wr_checkpt_validity_marker(dev, 0);
+	}
+
+	if (ok)
+		ok = yaffs2_wr_checkpt_sum(dev);
+
+	if (!yaffs_checkpt_close(dev))
+		ok = 0;
+
+	if (ok)
+		dev->is_checkpointed = 1;
+	else
+		dev->is_checkpointed = 0;
+
+	return dev->is_checkpointed;
+}
+
+static int yaffs2_rd_checkpt_data(struct yaffs_dev *dev)
+{
+	int ok = 1;
+
+	if (!dev->param.is_yaffs2)
+		ok = 0;
+
+	if (ok && dev->param.skip_checkpt_rd) {
+		yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+			"skipping checkpoint read");
+		ok = 0;
+	}
+
+	if (ok)
+		ok = yaffs2_checkpt_open(dev, 0); /* open for read */
+
+	if (ok) {
+		yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+			"read checkpoint validity");
+		ok = yaffs2_rd_checkpt_validity_marker(dev, 1);
+	}
+	if (ok) {
+		yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+			"read checkpoint device");
+		ok = yaffs2_rd_checkpt_dev(dev);
+	}
+	if (ok) {
+		yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+			"read checkpoint objects");
+		ok = yaffs2_rd_checkpt_objs(dev);
+	}
+	if (ok) {
+		yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+			"read checkpoint validity");
+		ok = yaffs2_rd_checkpt_validity_marker(dev, 0);
+	}
+
+	if (ok) {
+		ok = yaffs2_rd_checkpt_sum(dev);
+		yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+			"read checkpoint checksum %d", ok);
+	}
+
+	if (!yaffs_checkpt_close(dev))
+		ok = 0;
+
+	if (ok)
+		dev->is_checkpointed = 1;
+	else
+		dev->is_checkpointed = 0;
+
+	return ok ? 1 : 0;
+
+}
+
+void yaffs2_checkpt_invalidate(struct yaffs_dev *dev)
+{
+	if (dev->is_checkpointed || dev->blocks_in_checkpt > 0) {
+		dev->is_checkpointed = 0;
+		yaffs2_checkpt_invalidate_stream(dev);
+	}
+	if (dev->param.sb_dirty_fn)
+		dev->param.sb_dirty_fn(dev);
+}
+
+int yaffs_checkpoint_save(struct yaffs_dev *dev)
+{
+
+	yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+		"save entry: is_checkpointed %d",
+		dev->is_checkpointed);
+
+	yaffs_verify_objects(dev);
+	yaffs_verify_blocks(dev);
+	yaffs_verify_free_chunks(dev);
+
+	if (!dev->is_checkpointed) {
+		yaffs2_checkpt_invalidate(dev);
+		yaffs2_wr_checkpt_data(dev);
+	}
+
+	yaffs_trace(YAFFS_TRACE_CHECKPOINT | YAFFS_TRACE_MOUNT,
+		"save exit: is_checkpointed %d",
+		dev->is_checkpointed);
+
+	return dev->is_checkpointed;
+}
+
+int yaffs2_checkpt_restore(struct yaffs_dev *dev)
+{
+	int retval;
+	yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+		"restore entry: is_checkpointed %d",
+		dev->is_checkpointed);
+
+	retval = yaffs2_rd_checkpt_data(dev);
+
+	if (dev->is_checkpointed) {
+		yaffs_verify_objects(dev);
+		yaffs_verify_blocks(dev);
+		yaffs_verify_free_chunks(dev);
+	}
+
+	yaffs_trace(YAFFS_TRACE_CHECKPOINT,
+		"restore exit: is_checkpointed %d",
+		dev->is_checkpointed);
+
+	return retval;
+}
+
+int yaffs2_handle_hole(struct yaffs_obj *obj, loff_t new_size)
+{
+	/* if new_size > old_file_size.
+	 * We're going to be writing a hole.
+	 * If the hole is small then write zeros otherwise write a start of hole marker.
+	 */
+
+	loff_t old_file_size;
+	int increase;
+	int small_hole;
+	int result = YAFFS_OK;
+	struct yaffs_dev *dev = NULL;
+
+	u8 *local_buffer = NULL;
+
+	int small_increase_ok = 0;
+
+	if (!obj)
+		return YAFFS_FAIL;
+
+	if (obj->variant_type != YAFFS_OBJECT_TYPE_FILE)
+		return YAFFS_FAIL;
+
+	dev = obj->my_dev;
+
+	/* Bail out if not yaffs2 mode */
+	if (!dev->param.is_yaffs2)
+		return YAFFS_OK;
+
+	old_file_size = obj->variant.file_variant.file_size;
+
+	if (new_size <= old_file_size)
+		return YAFFS_OK;
+
+	increase = new_size - old_file_size;
+
+	if (increase < YAFFS_SMALL_HOLE_THRESHOLD * dev->data_bytes_per_chunk &&
+	    yaffs_check_alloc_available(dev, YAFFS_SMALL_HOLE_THRESHOLD + 1))
+		small_hole = 1;
+	else
+		small_hole = 0;
+
+	if (small_hole)
+		local_buffer = yaffs_get_temp_buffer(dev, __LINE__);
+
+	if (local_buffer) {
+		/* fill hole with zero bytes */
+		int pos = old_file_size;
+		int this_write;
+		int written;
+		memset(local_buffer, 0, dev->data_bytes_per_chunk);
+		small_increase_ok = 1;
+
+		while (increase > 0 && small_increase_ok) {
+			this_write = increase;
+			if (this_write > dev->data_bytes_per_chunk)
+				this_write = dev->data_bytes_per_chunk;
+			written =
+			    yaffs_do_file_wr(obj, local_buffer, pos, this_write,
+					     0);
+			if (written == this_write) {
+				pos += this_write;
+				increase -= this_write;
+			} else {
+				small_increase_ok = 0;
+                        }
+		}
+
+		yaffs_release_temp_buffer(dev, local_buffer, __LINE__);
+
+		/* If we were out of space then reverse any chunks we've added */
+		if (!small_increase_ok)
+			yaffs_resize_file_down(obj, old_file_size);
+	}
+
+	if (!small_increase_ok &&
+	    obj->parent &&
+	    obj->parent->obj_id != YAFFS_OBJECTID_UNLINKED &&
+	    obj->parent->obj_id != YAFFS_OBJECTID_DELETED) {
+		/* Write a hole start header with the old file size */
+		yaffs_update_oh(obj, NULL, 0, 1, 0, NULL);
+	}
+
+	return result;
+
+}
+
+struct yaffs_block_index {
+	int seq;
+	int block;
+};
+
+static int yaffs2_ybicmp(const void *a, const void *b)
+{
+	int aseq = ((struct yaffs_block_index *)a)->seq;
+	int bseq = ((struct yaffs_block_index *)b)->seq;
+	int ablock = ((struct yaffs_block_index *)a)->block;
+	int bblock = ((struct yaffs_block_index *)b)->block;
+	if (aseq == bseq)
+		return ablock - bblock;
+	else
+		return aseq - bseq;
+}
+
+int yaffs2_scan_backwards(struct yaffs_dev *dev)
+{
+	struct yaffs_ext_tags tags;
+	int blk;
+	int block_iter;
+	int start_iter;
+	int end_iter;
+	int n_to_scan = 0;
+
+	int chunk;
+	int result;
+	int c;
+	int deleted;
+	enum yaffs_block_state state;
+	struct yaffs_obj *hard_list = NULL;
+	struct yaffs_block_info *bi;
+	u32 seq_number;
+	struct yaffs_obj_hdr *oh;
+	struct yaffs_obj *in;
+	struct yaffs_obj *parent;
+	int n_blocks = dev->internal_end_block - dev->internal_start_block + 1;
+	int is_unlinked;
+	u8 *chunk_data;
+
+	int file_size;
+	int is_shrink;
+	int found_chunks;
+	int equiv_id;
+	int alloc_failed = 0;
+
+	struct yaffs_block_index *block_index = NULL;
+	int alt_block_index = 0;
+
+	yaffs_trace(YAFFS_TRACE_SCAN,
+		"yaffs2_scan_backwards starts  intstartblk %d intendblk %d...",
+		dev->internal_start_block, dev->internal_end_block);
+
+	dev->seq_number = YAFFS_LOWEST_SEQUENCE_NUMBER;
+
+	block_index = kmalloc(n_blocks * sizeof(struct yaffs_block_index),
+			GFP_NOFS);
+
+	if (!block_index) {
+		block_index =
+		    vmalloc(n_blocks * sizeof(struct yaffs_block_index));
+		alt_block_index = 1;
+	}
+
+	if (!block_index) {
+		yaffs_trace(YAFFS_TRACE_SCAN,
+			"yaffs2_scan_backwards() could not allocate block index!"
+			);
+		return YAFFS_FAIL;
+	}
+
+	dev->blocks_in_checkpt = 0;
+
+	chunk_data = yaffs_get_temp_buffer(dev, __LINE__);
+
+	/* Scan all the blocks to determine their state */
+	bi = dev->block_info;
+	for (blk = dev->internal_start_block; blk <= dev->internal_end_block;
+	     blk++) {
+		yaffs_clear_chunk_bits(dev, blk);
+		bi->pages_in_use = 0;
+		bi->soft_del_pages = 0;
+
+		yaffs_query_init_block_state(dev, blk, &state, &seq_number);
+
+		bi->block_state = state;
+		bi->seq_number = seq_number;
+
+		if (bi->seq_number == YAFFS_SEQUENCE_CHECKPOINT_DATA)
+			bi->block_state = state = YAFFS_BLOCK_STATE_CHECKPOINT;
+		if (bi->seq_number == YAFFS_SEQUENCE_BAD_BLOCK)
+			bi->block_state = state = YAFFS_BLOCK_STATE_DEAD;
+
+		yaffs_trace(YAFFS_TRACE_SCAN_DEBUG,
+			"Block scanning block %d state %d seq %d",
+			blk, state, seq_number);
+
+		if (state == YAFFS_BLOCK_STATE_CHECKPOINT) {
+			dev->blocks_in_checkpt++;
+
+		} else if (state == YAFFS_BLOCK_STATE_DEAD) {
+			yaffs_trace(YAFFS_TRACE_BAD_BLOCKS,
+				"block %d is bad", blk);
+		} else if (state == YAFFS_BLOCK_STATE_EMPTY) {
+			yaffs_trace(YAFFS_TRACE_SCAN_DEBUG, "Block empty ");
+			dev->n_erased_blocks++;
+			dev->n_free_chunks += dev->param.chunks_per_block;
+		} else if (state == YAFFS_BLOCK_STATE_NEEDS_SCANNING) {
+
+			/* Determine the highest sequence number */
+			if (seq_number >= YAFFS_LOWEST_SEQUENCE_NUMBER &&
+			    seq_number < YAFFS_HIGHEST_SEQUENCE_NUMBER) {
+
+				block_index[n_to_scan].seq = seq_number;
+				block_index[n_to_scan].block = blk;
+
+				n_to_scan++;
+
+				if (seq_number >= dev->seq_number)
+					dev->seq_number = seq_number;
+			} else {
+				/* TODO: Nasty sequence number! */
+				yaffs_trace(YAFFS_TRACE_SCAN,
+					"Block scanning block %d has bad sequence number %d",
+					blk, seq_number);
+
+			}
+		}
+		bi++;
+	}
+
+	yaffs_trace(YAFFS_TRACE_SCAN, "%d blocks to be sorted...", n_to_scan);
+
+	cond_resched();
+
+	/* Sort the blocks by sequence number */
+	sort(block_index, n_to_scan, sizeof(struct yaffs_block_index),
+		   yaffs2_ybicmp, NULL);
+
+	cond_resched();
+
+	yaffs_trace(YAFFS_TRACE_SCAN, "...done");
+
+	/* Now scan the blocks looking at the data. */
+	start_iter = 0;
+	end_iter = n_to_scan - 1;
+	yaffs_trace(YAFFS_TRACE_SCAN_DEBUG, "%d blocks to scan", n_to_scan);
+
+	/* For each block.... backwards */
+	for (block_iter = end_iter; !alloc_failed && block_iter >= start_iter;
+	     block_iter--) {
+		/* Cooperative multitasking! This loop can run for so
+		   long that watchdog timers expire. */
+		cond_resched();
+
+		/* get the block to scan in the correct order */
+		blk = block_index[block_iter].block;
+
+		bi = yaffs_get_block_info(dev, blk);
+
+		state = bi->block_state;
+
+		deleted = 0;
+
+		/* For each chunk in each block that needs scanning.... */
+		found_chunks = 0;
+		for (c = dev->param.chunks_per_block - 1;
+		     !alloc_failed && c >= 0 &&
+		     (state == YAFFS_BLOCK_STATE_NEEDS_SCANNING ||
+		      state == YAFFS_BLOCK_STATE_ALLOCATING); c--) {
+			/* Scan backwards...
+			 * Read the tags and decide what to do
+			 */
+
+			chunk = blk * dev->param.chunks_per_block + c;
+
+			result = yaffs_rd_chunk_tags_nand(dev, chunk, NULL,
+							  &tags);
+
+			/* Let's have a good look at this chunk... */
+
+			if (!tags.chunk_used) {
+				/* An unassigned chunk in the block.
+				 * If there are used chunks after this one, then
+				 * it is a chunk that was skipped due to failing the erased
+				 * check. Just skip it so that it can be deleted.
+				 * But, more typically, We get here when this is an unallocated
+				 * chunk and his means that either the block is empty or
+				 * this is the one being allocated from
+				 */
+
+				if (found_chunks) {
+					/* This is a chunk that was skipped due to failing the erased check */
+				} else if (c == 0) {
+					/* We're looking at the first chunk in the block so the block is unused */
+					state = YAFFS_BLOCK_STATE_EMPTY;
+					dev->n_erased_blocks++;
+				} else {
+					if (state ==
+					    YAFFS_BLOCK_STATE_NEEDS_SCANNING
+					    || state ==
+					    YAFFS_BLOCK_STATE_ALLOCATING) {
+						if (dev->seq_number ==
+						    bi->seq_number) {
+							/* this is the block being allocated from */
+
+							yaffs_trace(YAFFS_TRACE_SCAN,
+								" Allocating from %d %d",
+								blk, c);
+
+							state =
+							    YAFFS_BLOCK_STATE_ALLOCATING;
+							dev->alloc_block = blk;
+							dev->alloc_page = c;
+							dev->
+							    alloc_block_finder =
+							    blk;
+						} else {
+							/* This is a partially written block that is not
+							 * the current allocation block.
+							 */
+
+							yaffs_trace(YAFFS_TRACE_SCAN,
+								"Partially written block %d detected",
+								blk);
+						}
+					}
+				}
+
+				dev->n_free_chunks++;
+
+			} else if (tags.ecc_result == YAFFS_ECC_RESULT_UNFIXED) {
+				yaffs_trace(YAFFS_TRACE_SCAN,
+					" Unfixed ECC in chunk(%d:%d), chunk ignored",
+					blk, c);
+
+				dev->n_free_chunks++;
+
+			} else if (tags.obj_id > YAFFS_MAX_OBJECT_ID ||
+				   tags.chunk_id > YAFFS_MAX_CHUNK_ID ||
+				   (tags.chunk_id > 0
+				    && tags.n_bytes > dev->data_bytes_per_chunk)
+				   || tags.seq_number != bi->seq_number) {
+				yaffs_trace(YAFFS_TRACE_SCAN,
+					"Chunk (%d:%d) with bad tags:obj = %d, chunk_id = %d, n_bytes = %d, ignored",
+					blk, c, tags.obj_id,
+					tags.chunk_id, tags.n_bytes);
+
+				dev->n_free_chunks++;
+
+			} else if (tags.chunk_id > 0) {
+				/* chunk_id > 0 so it is a data chunk... */
+				unsigned int endpos;
+				u32 chunk_base =
+				    (tags.chunk_id -
+				     1) * dev->data_bytes_per_chunk;
+
+				found_chunks = 1;
+
+				yaffs_set_chunk_bit(dev, blk, c);
+				bi->pages_in_use++;
+
+				in = yaffs_find_or_create_by_number(dev,
+								    tags.obj_id,
+								    YAFFS_OBJECT_TYPE_FILE);
+				if (!in) {
+					/* Out of memory */
+					alloc_failed = 1;
+				}
+
+				if (in &&
+				    in->variant_type == YAFFS_OBJECT_TYPE_FILE
+				    && chunk_base <
+				    in->variant.file_variant.shrink_size) {
+					/* This has not been invalidated by a resize */
+					if (!yaffs_put_chunk_in_file
+					    (in, tags.chunk_id, chunk, -1)) {
+						alloc_failed = 1;
+					}
+
+					/* File size is calculated by looking at the data chunks if we have not
+					 * seen an object header yet. Stop this practice once we find an object header.
+					 */
+					endpos = chunk_base + tags.n_bytes;
+
+					if (!in->valid &&	/* have not got an object header yet */
+					    in->variant.file_variant.
+					    scanned_size < endpos) {
+						in->variant.file_variant.
+						    scanned_size = endpos;
+						in->variant.file_variant.
+						    file_size = endpos;
+					}
+
+				} else if (in) {
+					/* This chunk has been invalidated by a resize, or a past file deletion
+					 * so delete the chunk*/
+					yaffs_chunk_del(dev, chunk, 1,
+							__LINE__);
+
+				}
+			} else {
+				/* chunk_id == 0, so it is an ObjectHeader.
+				 * Thus, we read in the object header and make the object
+				 */
+				found_chunks = 1;
+
+				yaffs_set_chunk_bit(dev, blk, c);
+				bi->pages_in_use++;
+
+				oh = NULL;
+				in = NULL;
+
+				if (tags.extra_available) {
+					in = yaffs_find_or_create_by_number(dev,
+									    tags.
+									    obj_id,
+									    tags.
+									    extra_obj_type);
+					if (!in)
+						alloc_failed = 1;
+				}
+
+				if (!in ||
+				    (!in->valid && dev->param.disable_lazy_load)
+				    || tags.extra_shadows || (!in->valid
+							      && (tags.obj_id ==
+								  YAFFS_OBJECTID_ROOT
+								  || tags.
+								  obj_id ==
+								  YAFFS_OBJECTID_LOSTNFOUND)))
+				{
+
+					/* If we don't have  valid info then we need to read the chunk
+					 * TODO In future we can probably defer reading the chunk and
+					 * living with invalid data until needed.
+					 */
+
+					result = yaffs_rd_chunk_tags_nand(dev,
+									  chunk,
+									  chunk_data,
+									  NULL);
+
+					oh = (struct yaffs_obj_hdr *)chunk_data;
+
+					if (dev->param.inband_tags) {
+						/* Fix up the header if they got corrupted by inband tags */
+						oh->shadows_obj =
+						    oh->inband_shadowed_obj_id;
+						oh->is_shrink =
+						    oh->inband_is_shrink;
+					}
+
+					if (!in) {
+						in = yaffs_find_or_create_by_number(dev, tags.obj_id, oh->type);
+						if (!in)
+							alloc_failed = 1;
+					}
+
+				}
+
+				if (!in) {
+					/* TODO Hoosterman we have a problem! */
+					yaffs_trace(YAFFS_TRACE_ERROR,
+						"yaffs tragedy: Could not make object for object  %d at chunk %d during scan",
+						tags.obj_id, chunk);
+					continue;
+				}
+
+				if (in->valid) {
+					/* We have already filled this one.
+					 * We have a duplicate that will be discarded, but
+					 * we first have to suck out resize info if it is a file.
+					 */
+
+					if ((in->variant_type ==
+					     YAFFS_OBJECT_TYPE_FILE) && ((oh
+									  &&
+									  oh->
+									  type
+									  ==
+									  YAFFS_OBJECT_TYPE_FILE)
+									 ||
+									 (tags.
+									  extra_available
+									  &&
+									  tags.
+									  extra_obj_type
+									  ==
+									  YAFFS_OBJECT_TYPE_FILE)))
+					{
+						u32 this_size =
+						    (oh) ? oh->
+						    file_size :
+						    tags.extra_length;
+						u32 parent_obj_id =
+						    (oh) ? oh->parent_obj_id :
+						    tags.extra_parent_id;
+
+						is_shrink =
+						    (oh) ? oh->
+						    is_shrink :
+						    tags.extra_is_shrink;
+
+						/* If it is deleted (unlinked at start also means deleted)
+						 * we treat the file size as being zeroed at this point.
+						 */
+						if (parent_obj_id ==
+						    YAFFS_OBJECTID_DELETED
+						    || parent_obj_id ==
+						    YAFFS_OBJECTID_UNLINKED) {
+							this_size = 0;
+							is_shrink = 1;
+						}
+
+						if (is_shrink
+						    && in->variant.file_variant.
+						    shrink_size > this_size)
+							in->variant.
+							    file_variant.
+							    shrink_size =
+							    this_size;
+
+						if (is_shrink)
+							bi->has_shrink_hdr = 1;
+
+					}
+					/* Use existing - destroy this one. */
+					yaffs_chunk_del(dev, chunk, 1,
+							__LINE__);
+
+				}
+
+				if (!in->valid && in->variant_type !=
+				    (oh ? oh->type : tags.extra_obj_type))
+					yaffs_trace(YAFFS_TRACE_ERROR,
+						"yaffs tragedy: Bad object type, %d != %d, for object %d at chunk %d during scan",
+						oh ?
+						oh->type : tags.extra_obj_type,
+						in->variant_type, tags.obj_id,
+						chunk);
+
+				if (!in->valid &&
+				    (tags.obj_id == YAFFS_OBJECTID_ROOT ||
+				     tags.obj_id ==
+				     YAFFS_OBJECTID_LOSTNFOUND)) {
+					/* We only load some info, don't fiddle with directory structure */
+					in->valid = 1;
+
+					if (oh) {
+
+						in->yst_mode = oh->yst_mode;
+						yaffs_load_attribs(in, oh);
+						in->lazy_loaded = 0;
+					} else {
+						in->lazy_loaded = 1;
+                                        }
+					in->hdr_chunk = chunk;
+
+				} else if (!in->valid) {
+					/* we need to load this info */
+
+					in->valid = 1;
+					in->hdr_chunk = chunk;
+
+					if (oh) {
+						in->variant_type = oh->type;
+
+						in->yst_mode = oh->yst_mode;
+						yaffs_load_attribs(in, oh);
+
+						if (oh->shadows_obj > 0)
+							yaffs_handle_shadowed_obj
+							    (dev,
+							     oh->shadows_obj,
+							     1);
+
+						yaffs_set_obj_name_from_oh(in,
+									   oh);
+						parent =
+						    yaffs_find_or_create_by_number
+						    (dev, oh->parent_obj_id,
+						     YAFFS_OBJECT_TYPE_DIRECTORY);
+
+						file_size = oh->file_size;
+						is_shrink = oh->is_shrink;
+						equiv_id = oh->equiv_id;
+
+					} else {
+						in->variant_type =
+						    tags.extra_obj_type;
+						parent =
+						    yaffs_find_or_create_by_number
+						    (dev, tags.extra_parent_id,
+						     YAFFS_OBJECT_TYPE_DIRECTORY);
+						file_size = tags.extra_length;
+						is_shrink =
+						    tags.extra_is_shrink;
+						equiv_id = tags.extra_equiv_id;
+						in->lazy_loaded = 1;
+
+					}
+					in->dirty = 0;
+
+					if (!parent)
+						alloc_failed = 1;
+
+					/* directory stuff...
+					 * hook up to parent
+					 */
+
+					if (parent && parent->variant_type ==
+					    YAFFS_OBJECT_TYPE_UNKNOWN) {
+						/* Set up as a directory */
+						parent->variant_type =
+						    YAFFS_OBJECT_TYPE_DIRECTORY;
+						INIT_LIST_HEAD(&parent->
+							       variant.dir_variant.children);
+					} else if (!parent
+						   || parent->variant_type !=
+						   YAFFS_OBJECT_TYPE_DIRECTORY) {
+						/* Hoosterman, another problem....
+						 * We're trying to use a non-directory as a directory
+						 */
+
+						yaffs_trace(YAFFS_TRACE_ERROR,
+							"yaffs tragedy: attempting to use non-directory as a directory in scan. Put in lost+found."
+							);
+						parent = dev->lost_n_found;
+					}
+
+					yaffs_add_obj_to_dir(parent, in);
+
+					is_unlinked = (parent == dev->del_dir)
+					    || (parent == dev->unlinked_dir);
+
+					if (is_shrink) {
+						/* Mark the block as having a shrink header */
+						bi->has_shrink_hdr = 1;
+					}
+
+					/* Note re hardlinks.
+					 * Since we might scan a hardlink before its equivalent object is scanned
+					 * we put them all in a list.
+					 * After scanning is complete, we should have all the objects, so we run
+					 * through this list and fix up all the chains.
+					 */
+
+					switch (in->variant_type) {
+					case YAFFS_OBJECT_TYPE_UNKNOWN:
+						/* Todo got a problem */
+						break;
+					case YAFFS_OBJECT_TYPE_FILE:
+
+						if (in->variant.
+						    file_variant.scanned_size <
+						    file_size) {
+							/* This covers the case where the file size is greater
+							 * than where the data is
+							 * This will happen if the file is resized to be larger
+							 * than its current data extents.
+							 */
+							in->variant.
+							    file_variant.
+							    file_size =
+							    file_size;
+							in->variant.
+							    file_variant.
+							    scanned_size =
+							    file_size;
+						}
+
+						if (in->variant.file_variant.
+						    shrink_size > file_size)
+							in->variant.
+							    file_variant.
+							    shrink_size =
+							    file_size;
+
+						break;
+					case YAFFS_OBJECT_TYPE_HARDLINK:
+						if (!is_unlinked) {
+							in->variant.
+							    hardlink_variant.
+							    equiv_id = equiv_id;
+							in->hard_links.next =
+							    (struct list_head *)
+							    hard_list;
+							hard_list = in;
+						}
+						break;
+					case YAFFS_OBJECT_TYPE_DIRECTORY:
+						/* Do nothing */
+						break;
+					case YAFFS_OBJECT_TYPE_SPECIAL:
+						/* Do nothing */
+						break;
+					case YAFFS_OBJECT_TYPE_SYMLINK:
+						if (oh) {
+							in->variant.
+							    symlink_variant.
+							    alias =
+							    yaffs_clone_str(oh->
+									    alias);
+							if (!in->variant.
+							    symlink_variant.
+							    alias)
+								alloc_failed =
+								    1;
+						}
+						break;
+					}
+
+				}
+
+			}
+
+		}		/* End of scanning for each chunk */
+
+		if (state == YAFFS_BLOCK_STATE_NEEDS_SCANNING) {
+			/* If we got this far while scanning, then the block is fully allocated. */
+			state = YAFFS_BLOCK_STATE_FULL;
+		}
+
+		bi->block_state = state;
+
+		/* Now let's see if it was dirty */
+		if (bi->pages_in_use == 0 &&
+		    !bi->has_shrink_hdr &&
+		    bi->block_state == YAFFS_BLOCK_STATE_FULL) {
+			yaffs_block_became_dirty(dev, blk);
+		}
+
+	}
+
+	yaffs_skip_rest_of_block(dev);
+
+	if (alt_block_index)
+		vfree(block_index);
+	else
+		kfree(block_index);
+
+	/* Ok, we've done all the scanning.
+	 * Fix up the hard link chains.
+	 * We should now have scanned all the objects, now it's time to add these
+	 * hardlinks.
+	 */
+	yaffs_link_fixup(dev, hard_list);
+
+	yaffs_release_temp_buffer(dev, chunk_data, __LINE__);
+
+	if (alloc_failed)
+		return YAFFS_FAIL;
+
+	yaffs_trace(YAFFS_TRACE_SCAN, "yaffs2_scan_backwards ends");
+
+	return YAFFS_OK;
+}
diff --git a/fs/yaffs2/yaffs_yaffs2.h b/fs/yaffs2/yaffs_yaffs2.h
new file mode 100644
index 0000000..e1a9287
--- /dev/null
+++ b/fs/yaffs2/yaffs_yaffs2.h
@@ -0,0 +1,39 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+#ifndef __YAFFS_YAFFS2_H__
+#define __YAFFS_YAFFS2_H__
+
+#include "yaffs_guts.h"
+
+void yaffs_calc_oldest_dirty_seq(struct yaffs_dev *dev);
+void yaffs2_find_oldest_dirty_seq(struct yaffs_dev *dev);
+void yaffs2_clear_oldest_dirty_seq(struct yaffs_dev *dev,
+				   struct yaffs_block_info *bi);
+void yaffs2_update_oldest_dirty_seq(struct yaffs_dev *dev, unsigned block_no,
+				    struct yaffs_block_info *bi);
+int yaffs_block_ok_for_gc(struct yaffs_dev *dev, struct yaffs_block_info *bi);
+u32 yaffs2_find_refresh_block(struct yaffs_dev *dev);
+int yaffs2_checkpt_required(struct yaffs_dev *dev);
+int yaffs_calc_checkpt_blocks_required(struct yaffs_dev *dev);
+
+void yaffs2_checkpt_invalidate(struct yaffs_dev *dev);
+int yaffs2_checkpt_save(struct yaffs_dev *dev);
+int yaffs2_checkpt_restore(struct yaffs_dev *dev);
+
+int yaffs2_handle_hole(struct yaffs_obj *obj, loff_t new_size);
+int yaffs2_scan_backwards(struct yaffs_dev *dev);
+
+#endif
diff --git a/fs/yaffs2/yportenv.h b/fs/yaffs2/yportenv.h
new file mode 100644
index 0000000..8183425
--- /dev/null
+++ b/fs/yaffs2/yportenv.h
@@ -0,0 +1,70 @@
+/*
+ * YAFFS: Yet another Flash File System . A NAND-flash specific file system.
+ *
+ * Copyright (C) 2002-2010 Aleph One Ltd.
+ *   for Toby Churchill Ltd and Brightstar Engineering
+ *
+ * Created by Charles Manning <charles@aleph1.co.uk>
+ *
+ * This program is free software; you can redistribute it and/or modify
+ * it under the terms of the GNU Lesser General Public License version 2.1 as
+ * published by the Free Software Foundation.
+ *
+ * Note: Only YAFFS headers are LGPL, YAFFS C code is covered by GPL.
+ */
+
+#ifndef __YPORTENV_LINUX_H__
+#define __YPORTENV_LINUX_H__
+
+#include <linux/version.h>
+#include <linux/kernel.h>
+#include <linux/mm.h>
+#include <linux/sched.h>
+#include <linux/string.h>
+#include <linux/slab.h>
+#include <linux/vmalloc.h>
+#include <linux/xattr.h>
+#include <linux/list.h>
+#include <linux/types.h>
+#include <linux/fs.h>
+#include <linux/stat.h>
+#include <linux/sort.h>
+#include <linux/bitops.h>
+
+#define YCHAR char
+#define YUCHAR unsigned char
+#define _Y(x)     x
+
+#define YAFFS_LOSTNFOUND_NAME		"lost+found"
+#define YAFFS_LOSTNFOUND_PREFIX		"obj"
+
+
+#define YAFFS_ROOT_MODE			0755
+#define YAFFS_LOSTNFOUND_MODE		0700
+
+#define Y_CURRENT_TIME CURRENT_TIME.tv_sec
+#define Y_TIME_CONVERT(x) (x).tv_sec
+
+#define compile_time_assertion(assertion) \
+	({ int x = __builtin_choose_expr(assertion, 0, (void)0); (void) x; })
+
+
+#ifndef Y_DUMP_STACK
+#define Y_DUMP_STACK() dump_stack()
+#endif
+
+#define yaffs_trace(msk, fmt, ...) do { \
+	if(yaffs_trace_mask & (msk)) \
+		printk(KERN_DEBUG "yaffs: " fmt "\n", ##__VA_ARGS__); \
+} while(0)
+
+#ifndef YBUG
+#define YBUG() do {\
+	yaffs_trace(YAFFS_TRACE_BUG,\
+		"bug " __FILE__ " %d",\
+		__LINE__);\
+	Y_DUMP_STACK();\
+} while (0)
+#endif
+
+#endif
